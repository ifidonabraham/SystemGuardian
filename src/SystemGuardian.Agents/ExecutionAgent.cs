using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-06: Execution Agent.
/// The only agent that mutates process state. It must be called after Whitelist Guard approval.
/// </summary>
public class ExecutionAgent : IExecutionAgent
{
    public int AgentId => 6;
    public string AgentName => "Execution Agent";

    private const int WmClose = 0x0010;
    private const int WmQuit = 0x0012;
    private const uint ProcessSuspendResume = 0x0800;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Dictionary<int, ProcessPriorityClass> _originalPriorities = new();
    private readonly HashSet<int> _suspendedPids = new();

    private static readonly HashSet<string> SystemCriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "rundll32", "spoolsv", "MsMpEng", "audiodg", "fontdrvhost"
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task<ExecutionResult> ExecuteActionAsync(string tickId, RankedAction candidate, string approvedBy)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExecutionResult
        {
            TickId = tickId,
            TargetPid = candidate.Pid,
            TargetName = candidate.Name,
            ActionAttempted = candidate.RecommendedAction
        };

        try
        {
            if (!HasGuardApproval(approvedBy))
                return Fail(result, "FAILED", "Execution refused because Whitelist Guard approval was missing.");

            if (candidate.Pid <= 0 || string.IsNullOrWhiteSpace(candidate.Name))
                return Fail(result, "FAILED", "Execution refused because target PID or name is invalid.");

            using var process = GetTargetProcess(candidate.Pid, result);
            if (process == null)
            {
                result.ActionResult = "SUCCESS";
                result.ProcessAliveAfter = false;
                result.PlainEnglish = $"{candidate.Name} (PID {candidate.Pid}) is already gone; no action needed.";
                return result;
            }

            if (process.Id == Environment.ProcessId || IsSelfProcess(process))
                return Fail(result, "FAILED", "Execution refused because target is SystemGuardian itself.");

            if (IsSystemCritical(process.ProcessName))
                return Fail(result, "FAILED", $"Execution refused because {process.ProcessName} is system-critical.");

            if (IsCurrentForeground(process.Id))
                return Fail(result, "FAILED", $"Execution refused because {process.ProcessName} is currently foreground.");

            result = candidate.RecommendedAction switch
            {
                "THROTTLE" => ExecuteThrottle(process, result),
                "SUSPEND" => ExecuteSuspend(process, result),
                "GRACEFUL_CLOSE" => await ExecuteGracefulCloseAsync(process, result),
                "FORCE_KILL" => ExecuteForceKill(process, result),
                "RESUME" => ExecuteResume(process, result),
                "RESTORE_PRIORITY" => ExecuteRestorePriority(process, result),
                _ => Fail(result, "FAILED", $"Unsupported execution action: {candidate.RecommendedAction}")
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            result.ActionResult = "ACCESS_DENIED";
            result.ExecutionErrors.Add(ex.Message);
            result.PlainEnglish = $"Access denied while applying {candidate.RecommendedAction} to {candidate.Name}.";
        }
        catch (Win32Exception ex)
        {
            result.ActionResult = ex.NativeErrorCode == 5 ? "ACCESS_DENIED" : "FAILED";
            result.ExecutionErrors.Add($"{ex.Message} (Win32 {ex.NativeErrorCode})");
            result.PlainEnglish = $"Failed to apply {candidate.RecommendedAction} to {candidate.Name}: {ex.Message}.";
        }
        catch (Exception ex)
        {
            result.ActionResult = "FAILED";
            result.ExecutionErrors.Add(ex.Message);
            result.PlainEnglish = $"Failed to apply {candidate.RecommendedAction} to {candidate.Name}: {ex.Message}.";
        }
        finally
        {
            result.DurationMs = sw.Elapsed.TotalMilliseconds;
            if (string.IsNullOrWhiteSpace(result.PlainEnglish))
                result.PlainEnglish = $"{result.ActionResult}: {result.ActionAttempted} on {result.TargetName}.";
        }

        return result;
    }

    private ExecutionResult ExecuteThrottle(Process process, ExecutionResult result)
    {
        result.ActionMethod = "Process.PriorityClass";

        if (!_originalPriorities.ContainsKey(process.Id))
            _originalPriorities[process.Id] = process.PriorityClass;

        var targetPriority = process.PriorityClass switch
        {
            ProcessPriorityClass.RealTime => ProcessPriorityClass.Normal,
            ProcessPriorityClass.High => ProcessPriorityClass.BelowNormal,
            ProcessPriorityClass.AboveNormal => ProcessPriorityClass.BelowNormal,
            ProcessPriorityClass.Normal => ProcessPriorityClass.BelowNormal,
            _ => ProcessPriorityClass.Idle
        };

        process.PriorityClass = targetPriority;
        process.Refresh();

        result.ActionResult = process.PriorityClass == targetPriority ? "SUCCESS" : "FAILED";
        result.ProcessAliveAfter = IsAlive(process);
        result.CanBeReversed = true;
        result.ReverseAction = "RESTORE_PRIORITY";
        result.PlainEnglish = result.ActionResult == "SUCCESS"
            ? $"Reduced CPU priority for {process.ProcessName} (PID {process.Id}) to {targetPriority}."
            : $"Could not verify priority reduction for {process.ProcessName} (PID {process.Id}).";
        return result;
    }

    private ExecutionResult ExecuteRestorePriority(Process process, ExecutionResult result)
    {
        result.ActionMethod = "Process.PriorityClass";

        if (!_originalPriorities.TryGetValue(process.Id, out var original))
            return Fail(result, "FAILED", $"No stored priority exists for {process.ProcessName} (PID {process.Id}).");

        process.PriorityClass = original;
        _originalPriorities.Remove(process.Id);

        result.ActionResult = "SUCCESS";
        result.ProcessAliveAfter = IsAlive(process);
        result.CanBeReversed = false;
        result.PlainEnglish = $"Restored CPU priority for {process.ProcessName} (PID {process.Id}) to {original}.";
        return result;
    }

    private ExecutionResult ExecuteSuspend(Process process, ExecutionResult result)
    {
        result.ActionMethod = "NtSuspendProcess";

        using var handle = SafeProcessHandle.Open(process.Id, ProcessSuspendResume | ProcessQueryLimitedInformation);
        int status = NtSuspendProcess(handle.Value);
        if (status != 0)
            return Fail(result, "FAILED", $"NtSuspendProcess returned status 0x{status:X}.");

        _suspendedPids.Add(process.Id);
        result.ActionResult = "SUCCESS";
        result.ProcessAliveAfter = IsAlive(process);
        result.CanBeReversed = true;
        result.ReverseAction = "RESUME";
        result.PlainEnglish = $"Suspended {process.ProcessName} (PID {process.Id}); process memory and handles are preserved.";
        return result;
    }

    private ExecutionResult ExecuteResume(Process process, ExecutionResult result)
    {
        result.ActionMethod = "NtResumeProcess";

        using var handle = SafeProcessHandle.Open(process.Id, ProcessSuspendResume | ProcessQueryLimitedInformation);
        int status = NtResumeProcess(handle.Value);
        if (status != 0)
            return Fail(result, "FAILED", $"NtResumeProcess returned status 0x{status:X}.");

        _suspendedPids.Remove(process.Id);
        result.ActionResult = "SUCCESS";
        result.ProcessAliveAfter = IsAlive(process);
        result.CanBeReversed = false;
        result.PlainEnglish = $"Resumed {process.ProcessName} (PID {process.Id}).";
        return result;
    }

    private async Task<ExecutionResult> ExecuteGracefulCloseAsync(Process process, ExecutionResult result)
    {
        result.ActionMethod = "PostMessage(WM_CLOSE)";

        var windows = GetProcessWindows(process);
        if (windows.Count == 0 && process.MainWindowHandle != IntPtr.Zero)
            windows.Add(process.MainWindowHandle);

        if (windows.Count == 0)
            return Fail(result, "FAILED", $"{process.ProcessName} has no visible window to close gracefully.");

        int posted = 0;
        foreach (var window in windows.Distinct())
            if (PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero))
                posted++;

        if (posted == 0)
            return Fail(result, "FAILED", $"WM_CLOSE could not be posted to {process.ProcessName}.");

        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(3)))
        {
            foreach (var window in windows.Distinct())
                PostMessage(window, WmQuit, IntPtr.Zero, IntPtr.Zero);

            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(2)))
            {
                result.ActionResult = "TIMEOUT";
                result.ProcessAliveAfter = true;
                result.PlainEnglish = $"{process.ProcessName} did not close after WM_CLOSE/WM_QUIT; force kill requires separate approval.";
                return result;
            }
        }

        result.ActionResult = "SUCCESS";
        result.ProcessAliveAfter = false;
        result.CanBeReversed = false;
        result.PlainEnglish = $"Gracefully closed {process.ProcessName} (PID {process.Id}).";
        return result;
    }

    private ExecutionResult ExecuteForceKill(Process process, ExecutionResult result)
    {
        result.ActionMethod = "Process.Kill";

        if (IsCurrentForeground(process.Id))
            return Fail(result, "FAILED", $"Force kill refused because {process.ProcessName} became foreground.");

        process.Kill(entireProcessTree: false);
        if (!process.WaitForExit(1000))
        {
            result.ActionResult = "TIMEOUT";
            result.ProcessAliveAfter = true;
            result.PlainEnglish = $"Force kill was sent to {process.ProcessName}, but it is still running.";
            return result;
        }

        _originalPriorities.Remove(process.Id);
        _suspendedPids.Remove(process.Id);

        result.ActionResult = "SUCCESS";
        result.ProcessAliveAfter = false;
        result.CanBeReversed = false;
        result.PlainEnglish = $"Force killed {process.ProcessName} (PID {process.Id}).";
        return result;
    }

    private static Process? GetTargetProcess(int pid, ExecutionResult result)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            result.ExecutionErrors.Add("Target process was not found.");
            return null;
        }
    }

    private static ExecutionResult Fail(ExecutionResult result, string status, string message)
    {
        result.ActionResult = status;
        result.ProcessAliveAfter = ProcessExists(result.TargetPid);
        result.ExecutionErrors.Add(message);
        result.PlainEnglish = message;
        return result;
    }

    private static bool HasGuardApproval(string approvedBy)
    {
        return !string.IsNullOrWhiteSpace(approvedBy) &&
               (approvedBy.Contains("GUARD", StringComparison.OrdinalIgnoreCase) ||
                approvedBy.Contains("AGENT-07", StringComparison.OrdinalIgnoreCase) ||
                approvedBy.Contains("WHITELIST", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSystemCritical(string processName)
    {
        var normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return SystemCriticalProcesses.Contains(normalized);
    }

    private static bool IsSelfProcess(Process process)
    {
        return process.ProcessName.Contains("SystemGuardian", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentForeground(int pid)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        return foregroundPid == pid;
    }

    private static bool IsAlive(Process process)
    {
        try
        {
            process.Refresh();
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProcessExists(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            var waitTask = process.WaitForExitAsync();
            return await Task.WhenAny(waitTask, Task.Delay(timeout)) == waitTask;
        }
        catch
        {
            return !ProcessExists(process.Id);
        }
    }

    private static List<IntPtr> GetProcessWindows(Process process)
    {
        var windows = new List<IntPtr>();

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint ownerPid);
            if (ownerPid == process.Id && IsWindowVisible(hWnd))
                windows.Add(hWnd);

            return true;
        }, IntPtr.Zero);

        if (windows.Count == 0 && process.MainWindowHandle != IntPtr.Zero)
            windows.Add(process.MainWindowHandle);

        return windows;
    }

    private sealed class SafeProcessHandle : IDisposable
    {
        private SafeProcessHandle(IntPtr value) => Value = value;
        public IntPtr Value { get; }

        public static SafeProcessHandle Open(int pid, uint desiredAccess)
        {
            var handle = OpenProcess(desiredAccess, false, pid);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new SafeProcessHandle(handle);
        }

        public void Dispose()
        {
            if (Value != IntPtr.Zero)
                CloseHandle(Value);
        }
    }
}
