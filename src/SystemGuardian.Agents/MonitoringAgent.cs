using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-01: Monitoring Agent — Passive data collector.
/// Measures CPU, GPU, RAM, Disk, Network, and Thermal metrics every tick.
/// NEVER makes decisions. NEVER takes actions. Only measures and reports.
/// </summary>
public class MonitoringAgent : IMonitoringAgent
{
    public int AgentId => 1;
    public string AgentName => "Monitoring Agent";

    // ── Cached PerformanceCounter instances (created once in InitializeAsync) ──
    private PerformanceCounter? _cpuTotalCounter;
    private PerformanceCounter? _cpuFreqCounter;
    private PerformanceCounter? _ramAvailableCounter;
    private PerformanceCounter? _pagefileCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private PerformanceCounter? _diskQueueCounter;
    private PerformanceCounter[] _perCoreCounters = Array.Empty<PerformanceCounter>();

    // ── Network delta tracking ───────────────────────────────────────────────
    private long _prevTotalBytesSent;
    private long _prevTotalBytesRecv;
    private DateTime _prevNetTime = DateTime.MinValue;
    private string _bestAdapterName = "Unknown";

    // ── Ring buffer (last 60 snapshots ≈ 1–2 min) ───────────────────────────
    private readonly Queue<MetricSnapshot> _buffer = new();
    private const int BufferSize = 60;

    // ── Deduplication ────────────────────────────────────────────────────────
    private MetricSnapshot? _lastSnapshot;

    // ── Constants (read once at init) ────────────────────────────────────────
    private float _totalRamMb = -1;

    // ── Thresholds ───────────────────────────────────────────────────────────
    private const float CpuYellow = 70f, CpuRed = 85f, CpuCritical = 95f;
    private const float RamYellow = 75f, RamRed = 85f, RamCritical = 95f;
    private const float GpuYellow = 80f, GpuRed = 90f, GpuCritical = 95f;
    private const float DiskYellow = 75f, DiskRed = 85f, DiskCritical = 95f;
    private const float ThermalYellow = 70f, ThermalRed = 80f, ThermalCritical = 90f;

    // ── P/Invoke for true system RAM ─────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ─────────────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        try
        {
            // CPU counters
            _cpuTotalCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuFreqCounter  = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);

            // Per-core CPU counters
            int coreCount = Environment.ProcessorCount;
            _perCoreCounters = new PerformanceCounter[coreCount];
            for (int i = 0; i < coreCount; i++)
                _perCoreCounters[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);

            // RAM / Pagefile
            _ramAvailableCounter = new PerformanceCounter("Memory", "Available MBytes", null, true);
            _pagefileCounter     = new PerformanceCounter("Paging File", "% Usage", "_Total", true);

            // Disk
            _diskReadCounter  = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec",         "_Total", true);
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec",        "_Total", true);
            _diskQueueCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk Queue Length", "_Total", true);

            // Warm up all counters — first read always returns 0, must be discarded
            WarmUpCounters();

            // Read total physical RAM once (it never changes)
            _totalRamMb = ReadTotalPhysicalRamMb();

            // Seed network baseline so the first delta is valid
            SeedNetworkBaseline();

            await Task.Delay(1000); // Let counters stabilise after warmup
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MonitoringAgent] InitializeAsync error: {ex.Message}");
        }
    }

    public async Task<MetricSnapshot> CollectMetricsAsync(string tickId)
    {
        var snapshot = new MetricSnapshot { TickId = tickId };

        CollectCpu(snapshot);
        CollectRam(snapshot);
        CollectDisk(snapshot);
        CollectNetwork(snapshot);
        CollectGpu(snapshot);
        CollectThermal(snapshot);

        ValidateSnapshot(snapshot);
        DetectAnomalies(snapshot);
        CheckDuplication(snapshot);

        AddToBuffer(snapshot);
        _lastSnapshot = snapshot;

        return await Task.FromResult(snapshot);
    }

    // ── CPU ──────────────────────────────────────────────────────────────────

    private void CollectCpu(MetricSnapshot s)
    {
        try
        {
            s.Cpu.OverallPct = _cpuTotalCounter?.NextValue() ?? -1;

            s.Cpu.PerCorePct = new float[_perCoreCounters.Length];
            for (int i = 0; i < _perCoreCounters.Length; i++)
            {
                try { s.Cpu.PerCorePct[i] = _perCoreCounters[i].NextValue(); }
                catch { s.Cpu.PerCorePct[i] = -1; }
            }

            s.Cpu.ClockMhz = _cpuFreqCounter?.NextValue() ?? -1;
        }
        catch (Exception ex)
        {
            s.CollectionErrors.Add($"CPU collection error: {ex.Message}");
        }
    }

    // ── RAM ──────────────────────────────────────────────────────────────────

    private void CollectRam(MetricSnapshot s)
    {
        try
        {
            float availMb = _ramAvailableCounter?.NextValue() ?? -1;
            float totalMb = _totalRamMb > 0 ? _totalRamMb : ReadTotalPhysicalRamMb();

            s.Ram.TotalMb   = totalMb;
            s.Ram.UsedMb    = totalMb > 0 && availMb >= 0 ? totalMb - availMb : -1;
            s.Ram.UsagePct  = totalMb > 0 && s.Ram.UsedMb >= 0
                ? (s.Ram.UsedMb / totalMb) * 100f : -1;
            s.Ram.PagefilePct = _pagefileCounter?.NextValue() ?? -1;
        }
        catch (Exception ex)
        {
            s.CollectionErrors.Add($"RAM collection error: {ex.Message}");
        }
    }

    // ── Disk ─────────────────────────────────────────────────────────────────

    private void CollectDisk(MetricSnapshot s)
    {
        try
        {
            float readBytes  = _diskReadCounter?.NextValue() ?? -1;
            float writeBytes = _diskWriteCounter?.NextValue() ?? -1;

            s.Disk.ReadMbps    = readBytes  >= 0 ? readBytes  / (1024f * 1024f) : -1;
            s.Disk.WriteMbps   = writeBytes >= 0 ? writeBytes / (1024f * 1024f) : -1;
            s.Disk.QueueLength = _diskQueueCounter?.NextValue() ?? -1;
            s.Disk.Drives      = CollectDriveUsage();
        }
        catch (Exception ex)
        {
            s.CollectionErrors.Add($"Disk collection error: {ex.Message}");
        }
    }

    private static List<MetricSnapshot.DiskMetrics.DriveUsage> CollectDriveUsage()
    {
        var drives = new List<MetricSnapshot.DiskMetrics.DriveUsage>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                var used    = drive.TotalSize - drive.TotalFreeSpace;
                var usedPct = (float)(used / (double)drive.TotalSize * 100.0);
                drives.Add(new MetricSnapshot.DiskMetrics.DriveUsage
                {
                    Letter   = drive.Name[0].ToString(),
                    UsagePct = usedPct
                });
            }
        }
        catch { /* partial list is fine */ }
        return drives;
    }

    // ── Network ──────────────────────────────────────────────────────────────

    private void CollectNetwork(MetricSnapshot s)
    {
        try
        {
            var now = DateTime.UtcNow;

            long totalSent = 0, totalRecv = 0;
            string bestName = "Unknown";
            long mostActive = 0;

            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                try
                {
                    var stats = iface.GetIPv4Statistics();
                    totalSent += stats.BytesSent;
                    totalRecv += stats.BytesReceived;

                    long activity = stats.BytesSent + stats.BytesReceived;
                    if (activity > mostActive)
                    {
                        mostActive = activity;
                        bestName   = iface.Name;
                    }
                }
                catch { /* skip problematic adapters */ }
            }

            if (_prevNetTime != DateTime.MinValue)
            {
                double elapsedSec = (now - _prevNetTime).TotalSeconds;
                if (elapsedSec > 0)
                {
                    s.Network.SentBps = (float)Math.Max(0, (totalSent - _prevTotalBytesSent) / elapsedSec);
                    s.Network.RecvBps = (float)Math.Max(0, (totalRecv - _prevTotalBytesRecv) / elapsedSec);
                }
            }

            s.Network.Adapter       = bestName;
            _prevTotalBytesSent     = totalSent;
            _prevTotalBytesRecv     = totalRecv;
            _prevNetTime            = now;
            _bestAdapterName        = bestName;
        }
        catch (Exception ex)
        {
            s.CollectionErrors.Add($"Network collection error: {ex.Message}");
        }
    }

    // ── GPU (stub — requires LibreHardwareMonitor, not yet integrated) ───────

    private static void CollectGpu(MetricSnapshot s)
    {
        // LibreHardwareMonitor integration is a future step.
        // All GPU fields default to -1 (hardware unavailable / not yet monitored).
        s.Gpu.UtilisationPct = -1;
        s.Gpu.VramUsedMb     = -1;
        s.Gpu.VramTotalMb    = -1;
        s.Gpu.TempCelsius    = -1;
    }

    // ── Thermal (stub — requires LibreHardwareMonitor) ───────────────────────

    private static void CollectThermal(MetricSnapshot s)
    {
        // LibreHardwareMonitor integration is a future step.
        s.Thermal.CpuTempCelsius    = -1;
        s.Thermal.SystemTempCelsius = -1;
        s.Thermal.FanSpeedPct       = -1;
        s.Thermal.ThrottlingActive  = false;
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private static void ValidateSnapshot(MetricSnapshot s)
    {
        s.Cpu.OverallPct  = ClampPct(s.Cpu.OverallPct);
        s.Ram.UsagePct    = ClampPct(s.Ram.UsagePct);
        s.Ram.PagefilePct = ClampPct(s.Ram.PagefilePct);
        s.Gpu.UtilisationPct = ClampPct(s.Gpu.UtilisationPct);

        // Ensure MB values are non-negative (or -1 for unknown)
        if (s.Ram.UsedMb  < 0 && s.Ram.UsedMb  != -1) s.Ram.UsedMb  = -1;
        if (s.Ram.TotalMb < 0 && s.Ram.TotalMb != -1) s.Ram.TotalMb = -1;
        if (s.Disk.ReadMbps  < 0 && s.Disk.ReadMbps  != -1) s.Disk.ReadMbps  = -1;
        if (s.Disk.WriteMbps < 0 && s.Disk.WriteMbps != -1) s.Disk.WriteMbps = -1;

        // Thermal sanity (0–120°C is valid; anything else is sensor noise or unavailable)
        if (s.Thermal.CpuTempCelsius is > 120 or (< 0 and not (-1)))
            s.Thermal.CpuTempCelsius = -1;
    }

    private static float ClampPct(float v) =>
        v == -1 ? -1 : float.IsNaN(v) || float.IsInfinity(v) ? -1 : Math.Clamp(v, 0, 100);

    // ── Anomaly detection ────────────────────────────────────────────────────

    private void DetectAnomalies(MetricSnapshot s)
    {
        CheckThreshold(s, "CPU",  s.Cpu.OverallPct,  CpuYellow,  CpuRed,  CpuCritical);
        CheckThreshold(s, "RAM",  s.Ram.UsagePct,    RamYellow,  RamRed,  RamCritical);
        CheckThreshold(s, "GPU",  s.Gpu.UtilisationPct, GpuYellow, GpuRed, GpuCritical);
        CheckThreshold(s, "Thermal", s.Thermal.CpuTempCelsius, ThermalYellow, ThermalRed, ThermalCritical);

        if (s.Thermal.ThrottlingActive)
            s.Anomalies.TriggeredThresholds.Add("Thermal throttling active");

        if (_buffer.Count < 5) return;

        var recent = _buffer.ToArray();

        DetectSpike(s, "CPU",  recent, r => r.Cpu.OverallPct);
        DetectSpike(s, "RAM",  recent, r => r.Ram.UsagePct);

        DetectSustained(s, "CPU", recent, r => r.Cpu.OverallPct, CpuRed);
        DetectSustained(s, "RAM", recent, r => r.Ram.UsagePct,   RamRed);

        // Memory leak pattern: RAM steadily rising across last 10 snapshots
        if (_buffer.Count >= 10)
        {
            var last10 = recent.TakeLast(10).Select(r => r.Ram.UsagePct).Where(v => v >= 0).ToList();
            if (last10.Count >= 10 && IsMonotonicallyIncreasing(last10))
                s.Anomalies.SuspiciousPatterns.Add("RAM steadily rising — possible memory leak");
        }
    }

    private static void CheckThreshold(MetricSnapshot s, string name, float value,
        float yellow, float red, float critical)
    {
        if (value < 0) return; // unavailable
        if (value > critical)
            s.Anomalies.TriggeredThresholds.Add($"{name} Critical: {value:F1}%");
        else if (value > red)
            s.Anomalies.TriggeredThresholds.Add($"{name} Red: {value:F1}%");
        else if (value > yellow)
            s.Anomalies.TriggeredThresholds.Add($"{name} Yellow: {value:F1}%");
    }

    private static void DetectSpike(MetricSnapshot s, string name,
        MetricSnapshot[] recent, Func<MetricSnapshot, float> selector)
    {
        var last5 = recent.TakeLast(5).Select(selector).Where(v => v >= 0).ToList();
        if (last5.Count < 3) return;

        float avg = last5.Average();
        float current = selector(s);

        // Spike: > 20% above recent average AND above 20% absolute (ignore low-value noise)
        if (current >= 0 && avg > 5 && current > avg * 1.2f)
            s.Anomalies.SuspiciousPatterns.Add($"{name} spike: {current:F1}% vs avg {avg:F1}%");
    }

    private static void DetectSustained(MetricSnapshot s, string name,
        MetricSnapshot[] recent, Func<MetricSnapshot, float> selector, float threshold)
    {
        var last5 = recent.TakeLast(5).Select(selector).ToList();
        if (last5.All(v => v > threshold))
            s.Anomalies.SuspiciousPatterns.Add($"{name} sustained above {threshold}% for 5+ ticks");
    }

    private static bool IsMonotonicallyIncreasing(List<float> values)
    {
        for (int i = 1; i < values.Count; i++)
            if (values[i] < values[i - 1] - 2f) // allow tiny fluctuations of ±2%
                return false;
        return true;
    }

    // ── Deduplication ────────────────────────────────────────────────────────

    private void CheckDuplication(MetricSnapshot s)
    {
        if (_lastSnapshot == null) return;
        if (Math.Abs(s.Cpu.OverallPct   - _lastSnapshot.Cpu.OverallPct)  < 0.01f &&
            Math.Abs(s.Ram.UsagePct     - _lastSnapshot.Ram.UsagePct)    < 0.01f &&
            Math.Abs(s.Disk.ReadMbps    - _lastSnapshot.Disk.ReadMbps)   < 0.01f &&
            Math.Abs(s.Network.SentBps  - _lastSnapshot.Network.SentBps) < 1f)
        {
            s.Anomalies.SuspiciousPatterns.Add("Snapshot identical to previous — possible sensor stuck");
        }
    }

    // ── Buffer management ────────────────────────────────────────────────────

    private void AddToBuffer(MetricSnapshot snapshot)
    {
        _buffer.Enqueue(snapshot);
        while (_buffer.Count > BufferSize)
            _buffer.Dequeue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void WarmUpCounters()
    {
        try { _cpuTotalCounter?.NextValue(); } catch { }
        try { _cpuFreqCounter?.NextValue(); }  catch { }
        try { _ramAvailableCounter?.NextValue(); } catch { }
        try { _pagefileCounter?.NextValue(); } catch { }
        try { _diskReadCounter?.NextValue(); }  catch { }
        try { _diskWriteCounter?.NextValue(); } catch { }
        try { _diskQueueCounter?.NextValue(); } catch { }
        foreach (var c in _perCoreCounters)
            try { c.NextValue(); } catch { }
    }

    private static float ReadTotalPhysicalRamMb()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return (float)(status.ullTotalPhys / (1024.0 * 1024.0));
        }
        catch { }
        return -1;
    }

    private void SeedNetworkBaseline()
    {
        try
        {
            long sent = 0, recv = 0;
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = iface.GetIPv4Statistics();
                sent += stats.BytesSent;
                recv += stats.BytesReceived;
            }
            _prevTotalBytesSent = sent;
            _prevTotalBytesRecv = recv;
            _prevNetTime = DateTime.UtcNow;
        }
        catch { }
    }
}
