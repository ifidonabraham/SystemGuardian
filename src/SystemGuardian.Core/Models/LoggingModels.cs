using System;
using System.Collections.Generic;

namespace SystemGuardian.Core.Models;

/// <summary>
/// WriteConfirm: Confirmation from AGENT-08 Logger after writing to database
/// </summary>
public class WriteConfirm
{
    public string WriteId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; } = string.Empty;
    public string LogType { get; set; } = string.Empty; // TIER_CHANGE, ACTION_TAKEN, WHITELIST_BLOCKED, AGENT_ERROR, SYSTEM_RECOVERY
    public int RecordId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// FeedbackConfirm: Confirmation from AGENT-10 Feedback Agent
/// </summary>
public class FeedbackConfirm
{
    public string FeedbackId { get; set; } = Guid.NewGuid().ToString();
    public int ActionId { get; set; }
    public bool WasCorrect { get; set; }
    public bool Stored { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// ModelUpdateReport: Output from AGENT-10 after nightly model retraining
/// </summary>
public class ModelUpdateReport
{
    public string ReportId { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int SamplesUsed { get; set; }
    public float NewModelAccuracy { get; set; }
    public float OldModelAccuracy { get; set; }
    public string Decision { get; set; } = "SKIPPED"; // MODEL_UPDATED, MODEL_REJECTED, SKIPPED
    public string? SkipReason { get; set; }
    public Dictionary<string, Dictionary<string, int>> ConfusionMatrix { get; set; } = new();
}

/// <summary>
/// DisplayConfirm: Confirmation from AGENT-09 UI/Notification Agent
/// </summary>
public class DisplayConfirm
{
    public string DisplayId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; } = string.Empty;
    public string NotificationType { get; set; } = string.Empty; // TRAY_UPDATE, TOAST, DASHBOARD_REFRESH
    public bool Shown { get; set; }
    public string SuppressedReason { get; set; } = string.Empty;
    public string? Error { get; set; }
}

/// <summary>
/// OrchestratorDecision: Master output from AGENT-00 Orchestrator
/// </summary>
public class OrchestratorDecision
{
    public string DecisionId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public int CurrentTier { get; set; } // 1-4
    public int PreviousTier { get; set; }
    public bool TierChanged { get; set; }
    public List<int> ActiveAgents { get; set; } = new();
    public string WorstResource { get; set; }
    public float WorstResourcePct { get; set; }

    public ActionToExecute? ExecutedAction { get; set; }
    public bool ResumeAction { get; set; }
    public string PauseReason { get; set; }
    public List<string> DecisionReasons { get; set; } = new();

    public class ActionToExecute
    {
        public int TargetPid { get; set; }
        public string TargetName { get; set; }
        public string ApprovedAction { get; set; }
        public string ApprovedBy { get; set; }
    }
}
