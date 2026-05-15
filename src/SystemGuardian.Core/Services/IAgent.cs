using System.Threading.Tasks;
using SystemGuardian.Core.Models;

namespace SystemGuardian.Core.Services;

/// <summary>
/// Base interface for all agents in the system
/// </summary>
public interface IAgent
{
    int AgentId { get; }
    string AgentName { get; }
    Task InitializeAsync();
}

/// <summary>
/// AGENT-01: Monitoring Agent - Real-time system metric collection
/// </summary>
public interface IMonitoringAgent : IAgent
{
    /// <summary>Collect current system metrics snapshot</summary>
    Task<MetricSnapshot> CollectMetricsAsync(string tickId);
}

/// <summary>
/// AGENT-02: Forecasting Agent - 30-second forward prediction using ML.NET SSA
/// </summary>
public interface IForecastingAgent : IAgent
{
    /// <summary>Add metric to rolling buffer and forecast 30 seconds ahead</summary>
    Task<ForecastResult> ForecastAsync(string tickId, MetricSnapshot currentMetrics);
}

/// <summary>
/// AGENT-03: Process Tree Agent - Parent-child process hierarchy mapping
/// </summary>
public interface IProcessTreeAgent : IAgent
{
    /// <summary>Build complete process tree and safe kill ordering</summary>
    Task<ProcessTree> BuildProcessTreeAsync(string tickId);
}

/// <summary>
/// AGENT-04: Context Agent - User context and idle detection
/// </summary>
public interface IContextAgent : IAgent
{
    /// <summary>Detect foreground window and user idle state</summary>
    Task<ContextState> GetContextStateAsync(string tickId, ProcessTree processTree);
}

/// <summary>
/// AGENT-05: Action Ranker Agent - ML-based process danger scoring
/// </summary>
public interface IActionRankerAgent : IAgent
{
    /// <summary>Score all processes and rank by danger level</summary>
    Task<RankedList> RankProcessesAsync(string tickId, MetricSnapshot metrics, ProcessTree tree, ContextState context);
}

/// <summary>
/// AGENT-06: Execution Agent - Perform actual process control actions
/// </summary>
public interface IExecutionAgent : IAgent
{
    /// <summary>Execute approved action on target process</summary>
    Task<ExecutionResult> ExecuteActionAsync(string tickId, RankedAction action, string approvedBy);
}

/// <summary>
/// AGENT-07: Whitelist Guard - Safety approval gate before execution
/// </summary>
public interface IWhitelistGuardAgent : IAgent
{
    /// <summary>Approve or block proposed action</summary>
    Task<GuardDecision> ApproveActionAsync(string tickId, RankedAction candidate, ContextState context, ProcessTree tree);
}

/// <summary>
/// AGENT-08: Logger Agent - Persistent audit trail to SQLite
/// </summary>
public interface ILoggerAgent : IAgent
{
    /// <summary>Log action or event to database</summary>
    Task<WriteConfirm> LogAsync(string tickId, string logType, object payload);
}

/// <summary>
/// AGENT-09: UI/Notification Agent - User communication
/// </summary>
public interface IUINotificationAgent : IAgent
{
    /// <summary>Display notification to user (tray, toast, dashboard)</summary>
    Task<DisplayConfirm> NotifyAsync(string tickId, string notificationType, object payload);
}

/// <summary>
/// AGENT-10: Feedback Agent - Collect user feedback and model retraining
/// </summary>
public interface IFeedbackAgent : IAgent
{
    /// <summary>Record user feedback on action</summary>
    Task<FeedbackConfirm> RecordFeedbackAsync(int actionId, bool wasCorrect, string? userNote = null);

    /// <summary>Run nightly model retraining cycle</summary>
    Task<ModelUpdateReport> RetrainModelAsync();
}

/// <summary>
/// AGENT-00: Orchestrator Agent - Master coordinator
/// </summary>
public interface IOrchestratorAgent : IAgent
{
    /// <summary>Run one complete orchestration cycle</summary>
    Task<OrchestratorDecision> RunCycleAsync(string tickId);

    /// <summary>Get current system state/tier</summary>
    int GetCurrentTier();
}
