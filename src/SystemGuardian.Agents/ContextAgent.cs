using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-04: Context Agent.
/// Observes the user's foreground process, idle state, and protected process family.
/// It never recommends or executes actions; downstream agents use this as safety input.
/// </summary>
public class ContextAgent : IContextAgent
{
    public int AgentId => 4;
    public string AgentName => "Context Agent";

    private const int ActiveIdleLimitSeconds = 30;
    private const int IdleThresholdSeconds = 120;
    private const int VeryIdleThresholdSeconds = 1800;
    private const int RecentActivityWindowSeconds = 300;

    private readonly Queue<RecentActivity> _recentlyActive = new();

    private static readonly HashSet<string> IdeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "devenv", "rider64", "rider", "idea64", "pycharm64", "webstorm64",
        "phpstorm64", "clion64", "datagrip64", "sublime_text", "notepad++", "vim", "nvim", "emacs"
    };

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore"
    };

    private static readonly HashSet<string> CommunicationProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Teams", "ms-teams", "slack", "discord", "zoom", "outlook", "lync", "Skype"
    };

    private static readonly HashSet<string> MediaProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc", "wmplayer", "spotify", "itunes", "Music.UI", "Video.UI", "mpv"
    };

    private static readonly HashSet<string> OfficeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "winword", "excel", "powerpnt", "onenote", "acrord32", "Acrobat"
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    private sealed record RecentActivity(DateTime Timestamp, int Pid, string Name);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<ContextState> GetContextStateAsync(string tickId, ProcessTree processTree)
    {
        var sw = Stopwatch.StartNew();
        var context = new ContextState
        {
            TickId = tickId,
            CurrentUser = Environment.UserName,
            SessionType = Environment.UserInteractive ? "interactive" : "service"
        };

        try
        {
            DetectForeground(context, processTree);
            DetectIdleState(context);
            TrimRecentActivity();

            var nodeMap = processTree.Nodes.ToDictionary(n => n.Pid);
            context.ProtectedPids = BuildProtectedPids(context.ForegroundPid, nodeMap);
            context.RecentlyActivePids = GetRecentlyActivePids();

            AddRecentlyActiveProtections(context, nodeMap);
            ApplyApplicationContext(context);
            ApplySafetyAssessment(context);
            context.PlainEnglishSummary = BuildSummary(context);
        }
        catch (Exception ex)
        {
            context.ContextErrors.Add($"Context detection error: {ex.Message}");
            ApplyConservativeFallback(context, processTree);
        }

        sw.Stop();
        if (sw.ElapsedMilliseconds > 200)
            context.ContextErrors.Add($"Context build exceeded 200ms: {sw.ElapsedMilliseconds}ms");

        return Task.FromResult(context);
    }

    private void DetectForeground(ContextState context, ProcessTree processTree)
    {
        var foregroundHandle = GetForegroundWindow();
        if (foregroundHandle == IntPtr.Zero)
        {
            context.ForegroundPid = 0;
            context.ForegroundName = "Desktop";
            context.ForegroundAppType = "Desktop";
            context.IsForegroundProtected = false;
            context.ProtectionReason = "No foreground application window is active.";
            return;
        }

        uint threadId = GetWindowThreadProcessId(foregroundHandle, out uint foregroundPid);
        if (threadId == 0 || foregroundPid == 0)
        {
            context.ForegroundPid = 0;
            context.ForegroundName = "Unknown";
            context.ContextErrors.Add("Foreground window owner PID could not be resolved.");
            return;
        }

        context.ForegroundPid = checked((int)foregroundPid);
        context.ForegroundWindowTitle = ReadWindowText(foregroundHandle);
        context.ForegroundWindowClass = ReadWindowClass(foregroundHandle);
        context.ForegroundWindowVisible = IsWindowVisible(foregroundHandle);
        context.ForegroundWindowMinimized = IsIconic(foregroundHandle);
        context.ForegroundWindowMaximized = IsZoomed(foregroundHandle);

        if (!TryApplyProcessInfo(context, context.ForegroundPid))
            TryApplyTreeInfo(context, processTree);

        if (context.ForegroundPid > 0)
            RecordRecentActivity(context.ForegroundPid, context.ForegroundName);
    }

    private static bool TryApplyProcessInfo(ContextState context, int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            context.ForegroundName = process.ProcessName;
            context.SessionId = process.SessionId;
            try { context.ForegroundPath = process.MainModule?.FileName; } catch { }
            return true;
        }
        catch
        {
            context.ContextErrors.Add($"Could not resolve foreground process details for PID {pid}.");
            return false;
        }
    }

    private static void TryApplyTreeInfo(ContextState context, ProcessTree processTree)
    {
        var node = processTree.Nodes.FirstOrDefault(n => n.Pid == context.ForegroundPid);
        if (node == null) return;

        context.ForegroundName = string.IsNullOrWhiteSpace(node.Name) ? "Unknown" : node.Name;
        context.SessionId = node.SessionId;
        context.ForegroundWindowTitle ??= node.WindowTitle;
    }

    private static string? ReadWindowText(IntPtr hWnd)
    {
        var buffer = new StringBuilder(512);
        return GetWindowText(hWnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    private static string? ReadWindowClass(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hWnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    private static void DetectIdleState(ContextState context)
    {
        try
        {
            var lastInput = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref lastInput))
            {
                context.ContextErrors.Add("GetLastInputInfo failed; assuming user is active.");
                context.UserIdleSeconds = 0;
                context.UserIsIdle = false;
                context.IdleLevel = "ACTIVE";
                return;
            }

            uint idleMs = unchecked((uint)Environment.TickCount - lastInput.DwTime);
            context.UserIdleSeconds = idleMs / 1000.0f;
            context.UserIsIdle = context.UserIdleSeconds >= IdleThresholdSeconds;
            context.LastInputUtc = DateTime.UtcNow.AddSeconds(-context.UserIdleSeconds);
            context.IdleLevel = context.UserIdleSeconds switch
            {
                < ActiveIdleLimitSeconds => "ACTIVE",
                < IdleThresholdSeconds => "SEMI_ACTIVE",
                < VeryIdleThresholdSeconds => "IDLE",
                _ => "VERY_IDLE"
            };
        }
        catch (Exception ex)
        {
            context.ContextErrors.Add($"Idle detection failed: {ex.Message}. Assuming active user.");
            context.UserIdleSeconds = 0;
            context.UserIsIdle = false;
            context.IdleLevel = "ACTIVE";
        }
    }

    private static List<int> BuildProtectedPids(
        int foregroundPid,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap)
    {
        var protectedPids = new HashSet<int>();
        if (foregroundPid <= 0) return protectedPids.ToList();

        protectedPids.Add(foregroundPid);
        AddAncestors(foregroundPid, nodeMap, protectedPids);
        AddDescendants(foregroundPid, nodeMap, protectedPids);

        return protectedPids.OrderBy(pid => pid).ToList();
    }

    private static void AddAncestors(
        int pid,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        HashSet<int> result)
    {
        var seen = new HashSet<int>();
        var currentPid = pid;

        while (nodeMap.TryGetValue(currentPid, out var node) && node.ParentPid > 0)
        {
            if (!seen.Add(node.ParentPid)) break;
            result.Add(node.ParentPid);
            currentPid = node.ParentPid;
        }
    }

    private static void AddDescendants(
        int pid,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        HashSet<int> result)
    {
        if (!nodeMap.TryGetValue(pid, out var node)) return;

        foreach (int childPid in node.Children)
        {
            if (!result.Add(childPid)) continue;
            AddDescendants(childPid, nodeMap, result);
        }
    }

    private void AddRecentlyActiveProtections(
        ContextState context,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap)
    {
        var protectedPids = context.ProtectedPids.ToHashSet();
        foreach (int pid in context.RecentlyActivePids)
        {
            protectedPids.Add(pid);
            AddDescendants(pid, nodeMap, protectedPids);
        }

        context.ProtectedPids = protectedPids.OrderBy(pid => pid).ToList();
    }

    private void RecordRecentActivity(int pid, string name)
    {
        if (pid <= 0) return;

        _recentlyActive.Enqueue(new RecentActivity(DateTime.UtcNow, pid, name));
        TrimRecentActivity();
    }

    private void TrimRecentActivity()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-RecentActivityWindowSeconds);
        while (_recentlyActive.Count > 0 && _recentlyActive.Peek().Timestamp < cutoff)
            _recentlyActive.Dequeue();
    }

    private List<int> GetRecentlyActivePids()
    {
        return _recentlyActive
            .Select(x => x.Pid)
            .Distinct()
            .OrderBy(pid => pid)
            .ToList();
    }

    private static void ApplyApplicationContext(ContextState context)
    {
        string name = NormalizeProcessName(context.ForegroundName);
        context.ForegroundAppType = ClassifyApp(name);
        context.IsForegroundProtected = context.ForegroundPid > 0;

        context.ProtectionReason = context.ForegroundAppType switch
        {
            "IDE" => "User is working in an IDE/editor; foreground app and child tools are protected.",
            "Game" => "User is in a game or graphics-heavy app; foreground family is protected.",
            "Communication" => "User may be communicating; foreground app is protected.",
            "Media" => "User is consuming media; foreground app is protected.",
            "Browser" => "User is browsing; foreground browser process family is protected.",
            "Office" => "User may be editing a document; foreground office app is protected.",
            "Desktop" => "Desktop is active; no foreground application family is protected.",
            _ => context.ForegroundPid > 0
                ? "Current foreground application and related process family are protected."
                : "No foreground process is available."
        };
    }

    private static string ClassifyApp(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || processName == "desktop") return "Desktop";
        if (IdeProcesses.Contains(processName)) return "IDE";
        if (BrowserProcesses.Contains(processName)) return "Browser";
        if (CommunicationProcesses.Contains(processName)) return "Communication";
        if (MediaProcesses.Contains(processName)) return "Media";
        if (OfficeProcesses.Contains(processName)) return "Office";
        if (processName.Contains("unity", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("unreal", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("steam", StringComparison.OrdinalIgnoreCase))
            return "Game";

        return "Other";
    }

    private static string NormalizeProcessName(string processName)
    {
        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
    }

    private static void ApplySafetyAssessment(ContextState context)
    {
        context.CanThrottleProcesses = context.UserIdleSeconds >= ActiveIdleLimitSeconds;
        context.CanSuspendProcesses = context.UserIsIdle;
        context.CanKillProcesses = context.UserIdleSeconds >= 300;

        context.RecommendedContextAction = context.IdleLevel switch
        {
            "ACTIVE" => "notify user",
            "SEMI_ACTIVE" => "throttle background",
            "IDLE" => "suspend background",
            "VERY_IDLE" => "manage background",
            _ => "notify user"
        };

        context.UserAlertLevel = context.IdleLevel == "ACTIVE" ? "warn" : "none";
    }

    private static string BuildSummary(ContextState context)
    {
        if (context.ForegroundPid <= 0)
            return $"No foreground app detected. User idle level: {context.IdleLevel}.";

        return $"User foreground app is {context.ForegroundName} (PID {context.ForegroundPid}, {context.ForegroundAppType}). " +
               $"Idle for {context.UserIdleSeconds:F0}s; protecting {context.ProtectedPids.Count} related/recent process(es).";
    }

    private static void ApplyConservativeFallback(ContextState context, ProcessTree processTree)
    {
        context.ForegroundPid = context.ForegroundPid < 0 ? 0 : context.ForegroundPid;
        context.ForegroundName = string.IsNullOrWhiteSpace(context.ForegroundName)
            ? "Unknown"
            : context.ForegroundName;
        context.UserIdleSeconds = 0;
        context.UserIsIdle = false;
        context.IdleLevel = "ACTIVE";
        context.UserAlertLevel = "warn";
        context.RecommendedContextAction = "notify user";
        context.CanThrottleProcesses = false;
        context.CanSuspendProcesses = false;
        context.CanKillProcesses = false;

        if (context.ForegroundPid > 0)
        {
            var nodeMap = processTree.Nodes.ToDictionary(n => n.Pid);
            context.ProtectedPids = BuildProtectedPids(context.ForegroundPid, nodeMap);
        }

        context.PlainEnglishSummary = "Context detection degraded; assuming active user and preserving foreground process family.";
    }
}
