using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-03: Process Tree Agent — Windows parent-child hierarchy mapper.
/// Primary path: WMI Win32_Process (ProcessId, ParentProcessId, SessionId).
/// Fallback: NtQueryInformationProcess P/Invoke when WMI is unavailable.
/// Access-denied processes are marked IsSystem=true and included as stubs.
/// Results cached for 5 seconds; self (SystemGuardian) is always excluded.
/// NEVER makes decisions. Only maps structure.
/// </summary>
public class ProcessTreeAgent : IProcessTreeAgent
{
    public int AgentId => 3;
    public string AgentName => "Process Tree Agent";

    // ── System-critical process names (Process.ProcessName has no .exe suffix) ─
    private static readonly HashSet<string> SystemCriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "lsass", "csrss", "winlogon", "services",
        "smss", "wininit", "explorer", "System", "Registry",
        "MsMpEng", "spoolsv", "dwm", "ntoskrnl", "audiodg",
        "fontdrvhost", "SearchIndexer", "WmiPrvSE"
    };

    // ── Self-exclusion ────────────────────────────────────────────────────────
    private static readonly int _selfPid = Environment.ProcessId;

    // ── 5-second result cache ─────────────────────────────────────────────────
    private ProcessTree? _cachedTree;
    private DateTime _cacheBuiltAt = DateTime.MinValue;
    private const double CacheSeconds = 5.0;

    // ── P/Invoke: OpenProcess + NtQueryInformationProcess ─────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId; // PPID
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    // ─────────────────────────────────────────────────────────────────────────

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task<ProcessTree> BuildProcessTreeAsync(string tickId)
    {
        // Return cached tree if still fresh
        if (_cachedTree != null && (DateTime.UtcNow - _cacheBuiltAt).TotalSeconds < CacheSeconds)
        {
            _cachedTree.TickId = tickId;
            return await Task.FromResult(_cachedTree);
        }

        var tree = new ProcessTree { TickId = tickId };
        var sw   = Stopwatch.StartNew();

        try
        {
            // Step 1: WMI primary — parent PIDs and session IDs
            var (wmiParents, wmiSessions, wmiOk) = QueryWmi(tree.BuildErrors);

            // Step 2: enumerate all processes
            var allProcesses = Process.GetProcesses();

            // Step 3: build node map (PID → ProcessNode)
            var nodeMap = new Dictionary<int, ProcessTree.ProcessNode>(allProcesses.Length);

            foreach (var proc in allProcesses)
            {
                int pid = -1;
                try
                {
                    pid = proc.Id;
                    if (pid == _selfPid) continue;

                    int parentPid = wmiParents.TryGetValue(pid, out var wp) ? wp
                                  : (!wmiOk ? GetParentPidViaApi(pid) : 0);

                    int sessionId = wmiSessions.TryGetValue(pid, out var ws) ? ws : GetSessionId(proc);

                    bool isSystem = IsSystemCriticalName(proc.ProcessName)
                                 || sessionId == 0
                                 || IsAccessDenied(proc);

                    string? windowTitle = null;
                    try { windowTitle = proc.MainWindowTitle ?? ""; } catch { }

                    nodeMap[pid] = new ProcessTree.ProcessNode
                    {
                        Pid         = pid,
                        ParentPid   = parentPid,
                        Name        = proc.ProcessName,
                        Children    = new List<int>(),
                        Depth       = 0,
                        IsSystem    = isSystem,
                        WindowTitle = windowTitle,
                        SessionId   = sessionId
                    };
                }
                catch (Exception ex)
                {
                    // Access-denied: still include as system stub
                    if (pid > 0 && !nodeMap.ContainsKey(pid))
                    {
                        nodeMap[pid] = new ProcessTree.ProcessNode
                        {
                            Pid       = pid,
                            ParentPid = 0,
                            Name      = "(access denied)",
                            IsSystem  = true,
                            Children  = new List<int>()
                        };
                        tree.BuildErrors.Add($"PID {pid}: {ex.Message}");
                    }
                }
            }

            // Step 4: link parent → children; orphan if parent is absent or self
            foreach (var node in nodeMap.Values)
            {
                if (node.ParentPid == 0) continue;
                if (node.ParentPid == _selfPid) { node.ParentPid = 0; continue; }

                if (nodeMap.TryGetValue(node.ParentPid, out var parent))
                    parent.Children.Add(node.Pid);
                else
                    node.ParentPid = 0; // orphan — parent no longer exists
            }

            // Step 5: detect circular references before depth/order computation
            BreakCircularRefs(nodeMap, tree.BuildErrors);

            // Step 6: populate flat Nodes list
            tree.Nodes.AddRange(nodeMap.Values);
            tree.TotalProcesses = tree.Nodes.Count;

            // Step 7: BFS depth assignment from all root nodes (ParentPid == 0)
            var roots = nodeMap.Values.Where(n => n.ParentPid == 0).ToList();
            AssignDepthsBfs(roots, nodeMap);

            // Step 8: safe kill order per root — post-order DFS, system nodes excluded
            foreach (var node in nodeMap.Values)
                BuildSafeKillOrderDfs(node, nodeMap);
        }
        catch (Exception ex)
        {
            tree.BuildErrors.Add($"BuildProcessTreeAsync unhandled: {ex.Message}");
        }

        sw.Stop();
        if (sw.ElapsedMilliseconds > 500)
            tree.BuildErrors.Add($"Perf warning: tree built in {sw.ElapsedMilliseconds}ms");

        _cachedTree   = tree;
        _cacheBuiltAt = DateTime.UtcNow;

        return await Task.FromResult(tree);
    }

    // ── WMI primary query ─────────────────────────────────────────────────────

    private static (Dictionary<int, int> parents, Dictionary<int, int> sessions, bool ok) QueryWmi(
        List<string> errors)
    {
        var parents  = new Dictionary<int, int>();
        var sessions = new Dictionary<int, int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, SessionId FROM Win32_Process");

            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    int pid    = Convert.ToInt32(obj["ProcessId"]);
                    int parent = Convert.ToInt32(obj["ParentProcessId"]);
                    int sid    = Convert.ToInt32(obj["SessionId"]);
                    parents[pid]  = parent;
                    sessions[pid] = sid;
                }
                catch { }
            }
            return (parents, sessions, true);
        }
        catch (Exception ex)
        {
            errors.Add($"WMI failed, using NtQueryInformationProcess: {ex.Message}");
            return (parents, sessions, false);
        }
    }

    // ── NtQueryInformationProcess fallback ────────────────────────────────────

    private static int GetParentPidViaApi(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var pbi    = new ProcessBasicInformation();
            int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
            return status == 0 ? (int)pbi.InheritedFromUniqueProcessId.ToInt64() : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsSystemCriticalName(string processName)
    {
        var normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        return SystemCriticalNames.Contains(normalized);
    }

    private static bool IsAccessDenied(Process proc)
    {
        try
        {
            _ = proc.HandleCount;
            _ = proc.Threads.Count;
            _ = proc.PriorityClass;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static int GetSessionId(Process proc)
    {
        try { return proc.SessionId; }
        catch { return 0; }
    }

    private static bool IsElevatedOrDenied(Process proc)
    {
        try { return proc.PriorityClass == ProcessPriorityClass.RealTime; }
        catch { return true; } // access denied → treat as system
    }

    private static void AssignDepthsBfs(
        List<ProcessTree.ProcessNode> roots,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap)
    {
        var queue = new Queue<(ProcessTree.ProcessNode node, int depth)>();
        foreach (var root in roots)
            queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            node.Depth = depth;
            foreach (int childPid in node.Children)
                if (nodeMap.TryGetValue(childPid, out var child))
                    queue.Enqueue((child, depth + 1));
        }
    }

    private static void BuildSafeKillOrderDfs(
        ProcessTree.ProcessNode root,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap)
    {
        var order   = new List<int>();
        var visited = new HashSet<int>();
        PostOrderDfs(root, nodeMap, order, visited);
        root.SafeKillOrder = order;
    }

    private static void PostOrderDfs(
        ProcessTree.ProcessNode node,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        List<int> order,
        HashSet<int> visited)
    {
        if (!visited.Add(node.Pid)) return; // cycle guard

        foreach (int childPid in node.Children)
            if (nodeMap.TryGetValue(childPid, out var child))
                PostOrderDfs(child, nodeMap, order, visited);

        if (!node.IsSystem)
            order.Add(node.Pid);
    }

    private static void BreakCircularRefs(
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        List<string> errors)
    {
        var visited = new HashSet<int>();
        var inStack = new HashSet<int>();

        foreach (var node in nodeMap.Values)
            if (!visited.Contains(node.Pid))
                DfsBreakCircular(node, nodeMap, visited, inStack, errors);
    }

    private static void DfsBreakCircular(
        ProcessTree.ProcessNode node,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        HashSet<int> visited,
        HashSet<int> inStack,
        List<string> errors)
    {
        visited.Add(node.Pid);
        inStack.Add(node.Pid);

        foreach (int childPid in node.Children.ToList())
        {
            if (inStack.Contains(childPid))
            {
                errors.Add($"Circular reference broken: PID {node.Pid} -> PID {childPid}");
                node.Children.Remove(childPid);
                if (nodeMap.TryGetValue(childPid, out var child))
                    child.ParentPid = 0;
                continue;
            }

            if (!visited.Contains(childPid) && nodeMap.TryGetValue(childPid, out var childNode))
                DfsBreakCircular(childNode, nodeMap, visited, inStack, errors);
        }

        inStack.Remove(node.Pid);
    }

    private static void DetectCircularRefs(
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        List<string> errors)
    {
        var visited = new HashSet<int>();
        var inStack = new HashSet<int>();

        foreach (var node in nodeMap.Values)
            if (!visited.Contains(node.Pid))
                DfsCircular(node, nodeMap, visited, inStack, errors);
    }

    private static void DfsCircular(
        ProcessTree.ProcessNode node,
        Dictionary<int, ProcessTree.ProcessNode> nodeMap,
        HashSet<int> visited,
        HashSet<int> inStack,
        List<string> errors)
    {
        visited.Add(node.Pid);
        inStack.Add(node.Pid);

        foreach (int childPid in node.Children)
        {
            if (inStack.Contains(childPid))
            {
                errors.Add($"Circular reference: PID {node.Pid} → PID {childPid}");
                continue;
            }
            if (!visited.Contains(childPid) && nodeMap.TryGetValue(childPid, out var child))
                DfsCircular(child, nodeMap, visited, inStack, errors);
        }

        inStack.Remove(node.Pid);
    }
}
