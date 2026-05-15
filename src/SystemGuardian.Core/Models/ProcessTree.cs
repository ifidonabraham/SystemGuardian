using System;
using System.Collections.Generic;

namespace SystemGuardian.Core.Models;

/// <summary>
/// ProcessTree: Complete parent-child process hierarchy by AGENT-03
/// Producer: AGENT-03 (Process Tree Agent)
/// Consumers: AGENT-04, AGENT-05, AGENT-06, AGENT-07
/// </summary>
public class ProcessTree
{
    public string TreeId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int TotalProcesses { get; set; }
    public List<ProcessNode> Nodes { get; set; } = new();
    public List<string> BuildErrors { get; set; } = new();

    public class ProcessNode
    {
        public int Pid { get; set; }
        public int ParentPid { get; set; } // 0 if no parent
        public string Name { get; set; }
        public List<int> Children { get; set; } = new();
        public int Depth { get; set; }
        public bool IsSystem { get; set; }
        public string? WindowTitle { get; set; }
        public int SessionId { get; set; }
        public List<int> SafeKillOrder { get; set; } = new(); // Children first, then parents
    }
}
