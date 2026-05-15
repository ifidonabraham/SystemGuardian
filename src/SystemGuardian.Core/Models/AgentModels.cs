using System;
using System.Collections.Generic;

namespace SystemGuardian.Core.Models;

/// <summary>
/// ContextState: Current user context (foreground app, idle time) by AGENT-04
/// Producer: AGENT-04 (Context Agent)
/// Consumers: AGENT-05, AGENT-07, AGENT-09
/// </summary>
public class ContextState
{
    public string ContextId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public int ForegroundPid { get; set; }
    public string ForegroundName { get; set; } = "Unknown";
    public string? ForegroundPath { get; set; }
    public string? ForegroundWindowTitle { get; set; }
    public string? ForegroundWindowClass { get; set; }
    public bool ForegroundWindowVisible { get; set; }
    public bool ForegroundWindowMinimized { get; set; }
    public bool ForegroundWindowMaximized { get; set; }
    public float UserIdleSeconds { get; set; }
    public bool UserIsIdle { get; set; }
    public string IdleLevel { get; set; } = "ACTIVE";
    public DateTime? LastInputUtc { get; set; }
    public List<int> ProtectedPids { get; set; } = new();
    public List<int> RecentlyActivePids { get; set; } = new();
    public string ForegroundAppType { get; set; } = "Other";
    public bool IsForegroundProtected { get; set; } = true;
    public string ProtectionReason { get; set; } = "Foreground process is protected.";
    public string CurrentUser { get; set; } = Environment.UserName;
    public int SessionId { get; set; } = Environment.UserInteractive ? 1 : 0;
    public string SessionType { get; set; } = Environment.UserInteractive ? "interactive" : "service";
    public bool CanThrottleProcesses { get; set; }
    public bool CanSuspendProcesses { get; set; }
    public bool CanKillProcesses { get; set; }
    public string RecommendedContextAction { get; set; } = "notify user";
    public string UserAlertLevel { get; set; } = "none";
    public string PlainEnglishSummary { get; set; } = string.Empty;
    public List<string> ContextErrors { get; set; } = new();
}

/// <summary>
/// RankedAction: Single scored process candidate by AGENT-05
/// Part of RankedList output
/// </summary>
public class RankedAction
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public float DangerScore { get; set; } // 0-100
    public string RecommendedAction { get; set; } = "SAFE"; // SAFE, THROTTLE, SUSPEND, GRACEFUL_CLOSE, FORCE_KILL
    public string Reason { get; set; } = string.Empty;
    public Dictionary<string, float> Features { get; set; } = new(); // F1-F10 feature vector
}

/// <summary>
/// RankedList: Output from AGENT-05 with all scored process candidates
/// </summary>
public class RankedList
{
    public string RankId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<RankedAction> Candidates { get; set; } = new();
    public RankedAction? TopCandidate { get; set; }
    public List<string> RankingErrors { get; set; } = new();
}

/// <summary>
/// ExecutionResult: Outcome of action by AGENT-06
/// </summary>
public class ExecutionResult
{
    public string ExecutionId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public int TargetPid { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public string ActionAttempted { get; set; } = string.Empty; // THROTTLE, SUSPEND, GRACEFUL_CLOSE, FORCE_KILL
    public string ActionResult { get; set; } = "FAILED"; // SUCCESS, FAILED, TIMEOUT, ACCESS_DENIED
    public string? EscalatedTo { get; set; } // null or escalation action
    public string ActionMethod { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public bool ProcessAliveAfter { get; set; }
    public bool CanBeReversed { get; set; }
    public string? ReverseAction { get; set; }
    public string PlainEnglish { get; set; } = string.Empty;
    public List<string> ExecutionErrors { get; set; } = new();
}

/// <summary>
/// GuardDecision: Approval/rejection from AGENT-07 Whitelist Guard
/// </summary>
public class GuardDecision
{
    public string DecisionId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public int TargetPid { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public string Decision { get; set; } = "BLOCKED"; // APPROVED or BLOCKED
    public string ApprovalLevel { get; set; } = "DENY";
    public string BlockReason { get; set; } = string.Empty;
    public string BlockRuleTriggered { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = "HIGH";
    public bool RequiresUserConfirmation { get; set; }
    public string? ConfirmationPrompt { get; set; }
    public string? AlternativeAction { get; set; }
    public bool CanExecuteNow { get; set; }
    public int RecommendedWaitSeconds { get; set; }
    public List<string> ChecksPerformed { get; set; } = new();
    public List<string> RiskFactors { get; set; } = new();
    public double CheckDurationMs { get; set; }
}
