using System;
using System.Collections.Generic;

namespace SystemGuardian.Core.Models;

/// <summary>
/// MetricSnapshot: Real-time system resource metrics collected by AGENT-01 (Monitoring Agent)
/// Producer: AGENT-01 every 1-2 seconds
/// Consumers: AGENT-02 (forecasting), AGENT-05 (ranking), AGENT-08 (logging)
/// </summary>
public class MetricSnapshot
{
    public string SnapshotId { get; set; } = Guid.NewGuid().ToString();
    public string? TickId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public CpuMetrics Cpu { get; set; } = new();
    public GpuMetrics Gpu { get; set; } = new();
    public RamMetrics Ram { get; set; } = new();
    public DiskMetrics Disk { get; set; } = new();
    public NetworkMetrics Network { get; set; } = new();
    public ThermalMetrics Thermal { get; set; } = new();
    public AnomalyInfo Anomalies { get; set; } = new();

    public List<string> CollectionErrors { get; set; } = new();

    public class CpuMetrics
    {
        public float OverallPct { get; set; } = -1;
        public float[] PerCorePct { get; set; } = Array.Empty<float>();
        public float ClockMhz { get; set; } = -1;
    }

    public class GpuMetrics
    {
        public float UtilisationPct { get; set; } = -1;
        public float VramUsedMb { get; set; } = -1;
        public float VramTotalMb { get; set; } = -1;
        public float TempCelsius { get; set; } = -1;
    }

    public class RamMetrics
    {
        public float UsedMb { get; set; } = -1;
        public float TotalMb { get; set; } = -1;
        public float UsagePct { get; set; } = -1;
        public float PagefilePct { get; set; } = -1;
    }

    public class DiskMetrics
    {
        public float ReadMbps { get; set; } = -1;
        public float WriteMbps { get; set; } = -1;
        public float QueueLength { get; set; } = -1;
        public List<DriveUsage> Drives { get; set; } = new();

        public class DriveUsage
        {
            public string? Letter { get; set; }
            public float UsagePct { get; set; }
        }
    }

    public class NetworkMetrics
    {
        public float SentBps { get; set; } = -1;
        public float RecvBps { get; set; } = -1;
        public string? Adapter { get; set; }
    }

    public class ThermalMetrics
    {
        public float CpuTempCelsius { get; set; } = -1;
        public float SystemTempCelsius { get; set; } = -1;
        public float FanSpeedPct { get; set; } = -1;
        public bool ThrottlingActive { get; set; }
    }

    /// <summary>Anomalies detected at collection time — threshold crossings and suspicious patterns.</summary>
    public class AnomalyInfo
    {
        /// <summary>Which thresholds were crossed this tick (e.g. "CPU Red: 87.2%").</summary>
        public List<string> TriggeredThresholds { get; set; } = new();

        /// <summary>Unusual patterns detected (spikes, sustained high, memory leak, etc.).</summary>
        public List<string> SuspiciousPatterns { get; set; } = new();
    }
}
