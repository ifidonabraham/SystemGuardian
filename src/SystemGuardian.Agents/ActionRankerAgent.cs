using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-05: Action Ranker Agent.
/// Scores non-protected, non-system processes and recommends the safest graduated action.
/// Phase 1 uses deterministic rule-based scoring; SQLite trust data and ML are future inputs.
/// </summary>
public class ActionRankerAgent : IActionRankerAgent
{
    public int AgentId => 5;
    public string AgentName => "Action Ranker Agent";

    private const float ActionThreshold = 12f;
    private const int MaxCandidates = 50;

    private readonly HashSet<string> _processWhitelist = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int killCount, float trustScore)> _processHistory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, CpuSample> _cpuSamples = new();
    private readonly Dictionary<int, float> _lastCpuPct = new();

    private sealed record CpuSample(TimeSpan CpuTime, DateTime At);

    public Task InitializeAsync()
    {
        // Future: load user whitelist and trust scores from SQLite.
        return Task.CompletedTask;
    }

    public Task<RankedList> RankProcessesAsync(
        string tickId,
        MetricSnapshot metrics,
        ProcessTree tree,
        ContextState context)
    {
        var sw = Stopwatch.StartNew();
        var rankedList = new RankedList { TickId = tickId };

        try
        {
            var nodeMap = tree.Nodes.ToDictionary(n => n.Pid);
            var protectedPids = context.ProtectedPids.ToHashSet();
            var allProcesses = Process.GetProcesses();
            var livePids = allProcesses.Select(p => p.Id).ToHashSet();
            TrimCpuCache(livePids);

            var candidates = new List<RankedAction>();

            foreach (var process in allProcesses)
            {
                try
                {
                    if (ShouldSkipProcess(process, nodeMap, protectedPids))
                        continue;

                    var features = ComputeFeatureVector(process, metrics, nodeMap, context, protectedPids);
                    if (!AreFeaturesValid(features))
                    {
                        rankedList.RankingErrors.Add($"Invalid features for PID {process.Id}; skipped.");
                        continue;
                    }

                    var dangerScore = ComputeDangerScore(features, metrics);
                    var action = GetActionForScore(dangerScore, context, features);

                    if (dangerScore <= 0)
                        continue;

                    candidates.Add(new RankedAction
                    {
                        Pid = process.Id,
                        Name = process.ProcessName,
                        DangerScore = dangerScore,
                        RecommendedAction = action,
                        Reason = GenerateReason(process.ProcessName, features, dangerScore, action, context),
                        Features = features
                    });
                }
                catch (Exception ex)
                {
                    rankedList.RankingErrors.Add($"Error ranking PID {SafeProcessId(process)}: {ex.Message}");
                }
            }

            rankedList.Candidates = candidates
                .OrderByDescending(c => c.DangerScore)
                .ThenByDescending(c => c.Features.GetValueOrDefault("F1_cpu_pct"))
                .Take(MaxCandidates)
                .ToList();
            var pressure = Math.Max(metrics.Cpu.OverallPct, metrics.Ram.UsagePct);
            var effectiveThreshold = pressure >= 85f ? 12f : pressure >= 75f ? 16f : ActionThreshold;
            rankedList.TopCandidate = rankedList.Candidates
                .FirstOrDefault(c => c.DangerScore >= effectiveThreshold && c.RecommendedAction != "SAFE");

            if (rankedList.Candidates.Count == 0)
                rankedList.RankingErrors.Add("No scoreable non-protected, non-system process candidates found.");
            else if (rankedList.TopCandidate == null)
                rankedList.RankingErrors.Add("Candidates were scored, but none crossed the action threshold.");
        }
        catch (Exception ex)
        {
            rankedList.RankingErrors.Add($"Ranking error: {ex.Message}");
        }

        sw.Stop();
        if (sw.ElapsedMilliseconds > 300)
            rankedList.RankingErrors.Add($"Ranking exceeded 300ms: {sw.ElapsedMilliseconds}ms");

        return Task.FromResult(rankedList);
    }

    private bool ShouldSkipProcess(
        Process process,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        HashSet<int> protectedPids)
    {
        if (process.Id == Environment.ProcessId)
            return true;

        if (process.ProcessName.Contains("SystemGuardian", StringComparison.OrdinalIgnoreCase))
            return true;

        if (protectedPids.Contains(process.Id))
            return true;

        if (_processWhitelist.Contains(process.ProcessName))
            return true;

        return nodeMap.TryGetValue(process.Id, out var node) && node.IsSystem;
    }

    private Dictionary<string, float> ComputeFeatureVector(
        Process process,
        MetricSnapshot metrics,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        ContextState context,
        HashSet<int> protectedPids)
    {
        var features = new Dictionary<string, float>();

        float cpuPct = GetProcessCpuUsage(process);
        float previousCpuPct = _lastCpuPct.GetValueOrDefault(process.Id, cpuPct);
        _lastCpuPct[process.Id] = cpuPct;

        float ramMb = GetProcessRamMb(process);
        float totalRamMb = metrics.Ram.TotalMb > 0 ? metrics.Ram.TotalMb : 16384f;
        float ramPct = Math.Clamp(ramMb / totalRamMb * 100f, 0f, 100f);

        nodeMap.TryGetValue(process.Id, out var node);

        features["F1_cpu_pct"] = cpuPct;
        features["F2_ram_mb"] = ramMb;
        features["F2b_ram_pct"] = ramPct;
        features["F3_has_window"] = HasVisibleWindow(process) ? 1f : 0f;
        features["F4_is_foreground"] = context.ForegroundPid == process.Id ? 1f : 0f;
        features["F5_idle_seconds"] = context.RecentlyActivePids.Contains(process.Id) ? 0f : context.UserIdleSeconds;
        features["F6_priority"] = GetPriorityScore(process);
        features["F7_in_whitelist"] = _processWhitelist.Contains(process.ProcessName) ? 1f : 0f;
        features["F8_trust_score"] = GetTrustScore(process.ProcessName);
        features["F9_kill_count"] = GetKillCount(process.ProcessName);
        features["F10_child_of_protected"] = IsChildOfProtected(node, protectedPids) ? 1f : 0f;

        features["F11_child_count"] = node?.Children.Count ?? 0;
        features["F12_is_recently_active"] = context.RecentlyActivePids.Contains(process.Id) ? 1f : 0f;
        features["F13_cpu_trend"] = Math.Clamp(cpuPct - previousCpuPct, -100f, 100f);
        features["F14_session0"] = node?.SessionId == 0 ? 1f : 0f;
        features["F15_context_can_kill"] = context.CanKillProcesses ? 1f : 0f;
        features["F16_context_can_suspend"] = context.CanSuspendProcesses ? 1f : 0f;
        features["F17_sensitive_foreground_type"] = IsSensitiveForegroundType(context.ForegroundAppType) ? 1f : 0f;

        return features;
    }

    private float ComputeDangerScore(Dictionary<string, float> features, MetricSnapshot metrics)
    {
        if (features.GetValueOrDefault("F7_in_whitelist") >= 1f ||
            features.GetValueOrDefault("F4_is_foreground") >= 1f ||
            features.GetValueOrDefault("F10_child_of_protected") >= 1f)
            return 0f;

        float cpuPct = features.GetValueOrDefault("F1_cpu_pct");
        float ramPct = features.GetValueOrDefault("F2b_ram_pct");
        float trust = features.GetValueOrDefault("F8_trust_score", 0.5f);
        float childCount = features.GetValueOrDefault("F11_child_count");
        float idleSeconds = features.GetValueOrDefault("F5_idle_seconds");
        float cpuTrend = features.GetValueOrDefault("F13_cpu_trend");

        var systemPressure = Math.Max(metrics.Cpu.OverallPct, metrics.Ram.UsagePct);
        var ramWeight = metrics.Ram.UsagePct >= 85f ? 1.15f : metrics.Ram.UsagePct >= 75f ? 0.75f : 0.35f;
        var cpuWeight = metrics.Cpu.OverallPct >= 85f ? 0.75f : 0.55f;

        float score = 0f;
        score += Math.Clamp(cpuPct, 0f, 100f) * cpuWeight;
        score += Math.Clamp(ramPct, 0f, 100f) * ramWeight;
        score += Math.Max(0f, systemPressure - 70f) * 0.35f;
        score += features.GetValueOrDefault("F3_has_window") == 0f ? 10f : 0f;
        score += idleSeconds >= 300 ? 8f : idleSeconds >= 120 ? 4f : 0f;
        score += cpuTrend > 5f ? 6f : 0f;
        score += features.GetValueOrDefault("F9_kill_count") * 3f;

        score -= (1f - trust) * 15f;
        score -= Math.Min(childCount, 10f) * 2f;
        score -= features.GetValueOrDefault("F12_is_recently_active") * 20f;
        score -= features.GetValueOrDefault("F14_session0") * 40f;
        score -= features.GetValueOrDefault("F17_sensitive_foreground_type") * 5f;

        if (metrics.Cpu.OverallPct < 65f && metrics.Ram.UsagePct < 65f)
            score *= 0.65f;

        return Math.Clamp(score, 0f, 100f);
    }

    private static string GetActionForScore(
        float score,
        ContextState context,
        Dictionary<string, float> features)
    {
        if (score < ActionThreshold)
            return "SAFE";

        string action = score switch
        {
            >= 71f => "GRACEFUL_CLOSE",
            >= 51f => "SUSPEND",
            _ => "THROTTLE"
        };

        if (action == "SUSPEND" && !context.CanSuspendProcesses)
            action = "THROTTLE";

        if (action == "GRACEFUL_CLOSE" && !context.CanKillProcesses)
            action = context.CanSuspendProcesses ? "SUSPEND" : "THROTTLE";

        if (features.GetValueOrDefault("F11_child_count") >= 5 && action == "GRACEFUL_CLOSE")
            action = "SUSPEND";

        return action;
    }

    private string GenerateReason(
        string processName,
        Dictionary<string, float> features,
        float dangerScore,
        string action,
        ContextState context)
    {
        float cpu = features.GetValueOrDefault("F1_cpu_pct");
        float ramMb = features.GetValueOrDefault("F2_ram_mb");
        float ramPct = features.GetValueOrDefault("F2b_ram_pct");
        float idle = features.GetValueOrDefault("F5_idle_seconds");
        float childCount = features.GetValueOrDefault("F11_child_count");

        return $"{processName} is using {cpu:F1}% CPU and {ramMb:F0}MB RAM ({ramPct:F1}% of system memory). " +
               $"It is a background process, idle for {idle:F0}s, with {childCount:F0} child process(es). " +
               $"User context is {context.IdleLevel}; recommended action: {action} (score {dangerScore:F0}).";
    }

    private float GetProcessCpuUsage(Process process)
    {
        try
        {
            var now = DateTime.UtcNow;
            var current = new CpuSample(process.TotalProcessorTime, now);

            if (!_cpuSamples.TryGetValue(process.Id, out var previous))
            {
                _cpuSamples[process.Id] = current;
                return 0f;
            }

            _cpuSamples[process.Id] = current;

            double elapsedMs = (current.At - previous.At).TotalMilliseconds;
            if (elapsedMs <= 0) return 0f;

            double cpuMs = (current.CpuTime - previous.CpuTime).TotalMilliseconds;
            double pct = cpuMs / elapsedMs / Environment.ProcessorCount * 100.0;
            return (float)Math.Clamp(pct, 0.0, 100.0);
        }
        catch
        {
            return 0f;
        }
    }

    private static float GetProcessRamMb(Process process)
    {
        try { return process.WorkingSet64 / (1024f * 1024f); }
        catch { return 0f; }
    }

    private static bool HasVisibleWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != IntPtr.Zero &&
                   !string.IsNullOrWhiteSpace(process.MainWindowTitle);
        }
        catch
        {
            return false;
        }
    }

    private static float GetPriorityScore(Process process)
    {
        try
        {
            return process.PriorityClass switch
            {
                ProcessPriorityClass.Idle => 0f,
                ProcessPriorityClass.BelowNormal => 1f,
                ProcessPriorityClass.Normal => 2f,
                ProcessPriorityClass.AboveNormal => 3f,
                ProcessPriorityClass.High => 3f,
                ProcessPriorityClass.RealTime => 4f,
                _ => 2f
            };
        }
        catch
        {
            return 2f;
        }
    }

    private float GetTrustScore(string processName)
    {
        return _processHistory.TryGetValue(processName, out var history)
            ? Math.Clamp(history.trustScore, 0f, 1f)
            : 0.5f;
    }

    private float GetKillCount(string processName)
    {
        return _processHistory.TryGetValue(processName, out var history)
            ? history.killCount
            : 0f;
    }

    private static bool IsChildOfProtected(ProcessTree.ProcessNode? node, HashSet<int> protectedPids)
    {
        return node != null && node.ParentPid > 0 && protectedPids.Contains(node.ParentPid);
    }

    private static bool AreFeaturesValid(Dictionary<string, float> features)
    {
        return features.Values.All(v => !float.IsNaN(v) && !float.IsInfinity(v));
    }

    private static bool IsSensitiveForegroundType(string foregroundAppType)
    {
        return foregroundAppType is "IDE" or "Game" or "Communication" or "Office";
    }

    private void TrimCpuCache(HashSet<int> livePids)
    {
        foreach (int pid in _cpuSamples.Keys.Where(pid => !livePids.Contains(pid)).ToList())
        {
            _cpuSamples.Remove(pid);
            _lastCpuPct.Remove(pid);
        }
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }
}
