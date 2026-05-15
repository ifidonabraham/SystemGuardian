using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-00: Orchestrator Agent — Master coordinator and decision dispatcher.
/// Does NOT perform system actions. Dispatches work through downstream agents
/// in strict sequence, validates outputs, and maintains the complete decision trail.
/// </summary>
public class OrchestratorAgent : IOrchestratorAgent
{
    public int AgentId => 0;
    public string AgentName => "Orchestrator Agent";

    private readonly IMonitoringAgent _monitoringAgent;
    private readonly IForecastingAgent _forecastingAgent;
    private readonly IProcessTreeAgent _processTreeAgent;
    private readonly IContextAgent _contextAgent;
    private readonly IActionRankerAgent _actionRankerAgent;
    private readonly IExecutionAgent _executionAgent;
    private readonly IWhitelistGuardAgent _whitelistGuardAgent;
    private readonly ILoggerAgent _loggerAgent;
    private readonly IUINotificationAgent _uiAgent;
    private readonly IFeedbackAgent _feedbackAgent;

    // Rolling state buffers
    private readonly Queue<MetricSnapshot> _metricsBuffer = new();
    private readonly Queue<OrchestratorDecision> _decisionHistory = new();

    // Escalation / debounce tracking
    private readonly Dictionary<string, int> _escalationCounters = new();
    private DateTime _lastActionTime = DateTime.MinValue;
    private string _lastActionProcessName;

    // Tier state
    private int _currentTier = 1;
    private int _previousTier = 1;

    private const int MetricsBufferSize = 60;       // ~1-2 minutes of history
    private const int DecisionHistorySize = 1000;
    private const double DebounceWindowSeconds = 5.0;
    private const int AgentTimeoutMs = 2000;

    public OrchestratorAgent(
        IMonitoringAgent monitoringAgent,
        IForecastingAgent forecastingAgent,
        IProcessTreeAgent processTreeAgent,
        IContextAgent contextAgent,
        IActionRankerAgent actionRankerAgent,
        IExecutionAgent executionAgent,
        IWhitelistGuardAgent whitelistGuardAgent,
        ILoggerAgent loggerAgent,
        IUINotificationAgent uiAgent,
        IFeedbackAgent feedbackAgent)
    {
        _monitoringAgent = monitoringAgent;
        _forecastingAgent = forecastingAgent;
        _processTreeAgent = processTreeAgent;
        _contextAgent = contextAgent;
        _actionRankerAgent = actionRankerAgent;
        _executionAgent = executionAgent;
        _whitelistGuardAgent = whitelistGuardAgent;
        _loggerAgent = loggerAgent;
        _uiAgent = uiAgent;
        _feedbackAgent = feedbackAgent;
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _monitoringAgent.InitializeAsync(),
            _forecastingAgent.InitializeAsync(),
            _processTreeAgent.InitializeAsync(),
            _contextAgent.InitializeAsync(),
            _actionRankerAgent.InitializeAsync(),
            _executionAgent.InitializeAsync(),
            _whitelistGuardAgent.InitializeAsync(),
            _loggerAgent.InitializeAsync(),
            _uiAgent.InitializeAsync(),
            _feedbackAgent.InitializeAsync());
    }

    /// <summary>
    /// Run one complete orchestration cycle. Must complete in under 500ms.
    /// </summary>
    public async Task<OrchestratorDecision> RunCycleAsync(string tickId)
    {
        var sw = Stopwatch.StartNew();
        var decision = new OrchestratorDecision
        {
            TickId = tickId,
            PreviousTier = _currentTier
        };

        // ── Step 1: Monitoring (MANDATORY — always runs first) ──────────────
        var metrics = await RunWithTimeoutAsync(
            () => _monitoringAgent.CollectMetricsAsync(tickId),
            "Monitoring", tickId);

        if (metrics == null || !IsMetricsValid(metrics))
        {
            decision.DecisionReasons.Add("Metrics unavailable or invalid — skipping cycle");
            return decision;
        }

        AddToMetricsBuffer(metrics);
        decision.ActiveAgents.Add(1);
        decision.DecisionReasons.Add($"CPU {metrics.Cpu.OverallPct:F1}% | RAM {metrics.Ram.UsagePct:F1}%");
        _ = _loggerAgent.LogAsync(tickId, "METRIC_SNAPSHOT", metrics);

        // ── Step 2: Forecasting ─────────────────────────────────────────────
        var forecast = await RunWithTimeoutAsync(
            () => _forecastingAgent.ForecastAsync(tickId, metrics),
            "Forecasting", tickId);

        _previousTier = _currentTier;

        if (forecast != null)
        {
            decision.ActiveAgents.Add(2);
            decision.WorstResource = forecast.WorstResource;
            _ = _loggerAgent.LogAsync(tickId, "FORECAST_RESULT", forecast);
            if (forecast.Resources != null &&
                forecast.WorstResource != null &&
                forecast.Resources.TryGetValue(forecast.WorstResource, out var rf))
            {
                decision.WorstResourcePct = rf.ProjectedPct;
            }
            _currentTier = forecast.RecommendedTier;
        }
        else
        {
            // Fallback: rule-based tier from live metrics when forecast unavailable
            _currentTier = DetermineRuleBasedTier(metrics);
            decision.DecisionReasons.Add("Forecast unavailable — rule-based tier used");
        }

        decision.CurrentTier = _currentTier;

        if (_currentTier != _previousTier)
        {
            decision.TierChanged = true;
            decision.DecisionReasons.Add($"Tier {_previousTier} → {_currentTier}");
            _ = RunWithTimeoutAsync(
                () => _loggerAgent.LogAsync(tickId, "TIER_CHANGE", new { from = _previousTier, to = _currentTier }),
                "Logger", tickId);
        }

        // ── Steps 3-7: Only run if there's something to act on ─────────────
        if (_currentTier >= 2)
        {
            // Step 3: Process Tree
            var tree = await RunWithTimeoutAsync(
                () => _processTreeAgent.BuildProcessTreeAsync(tickId),
                "ProcessTree", tickId);

            if (tree != null) decision.ActiveAgents.Add(3);

            // Step 4: User Context
            var ctx = tree != null
                ? await RunWithTimeoutAsync(
                    () => _contextAgent.GetContextStateAsync(tickId, tree),
                    "Context", tickId)
                : null;

            if (ctx != null) decision.ActiveAgents.Add(4);

            // Step 5: Action Ranking
            var ranked = (tree != null && ctx != null)
                ? await RunWithTimeoutAsync(
                    () => _actionRankerAgent.RankProcessesAsync(tickId, metrics, tree, ctx),
                    "ActionRanker", tickId)
                : null;

            if (ranked != null) decision.ActiveAgents.Add(5);
            if (ranked != null)
            {
                if (ranked.TopCandidate != null)
                {
                    decision.DecisionReasons.Add(
                        $"Top candidate: {ranked.TopCandidate.Name} PID {ranked.TopCandidate.Pid}, " +
                        $"{ranked.TopCandidate.RecommendedAction}, score {ranked.TopCandidate.DangerScore:F1}");
                }
                else
                {
                    decision.DecisionReasons.Add("No executable candidate selected by ranker.");
                    foreach (var error in ranked.RankingErrors.Take(3))
                        decision.DecisionReasons.Add($"Ranker: {error}");
                    foreach (var candidate in ranked.Candidates.Take(3))
                        decision.DecisionReasons.Add(
                            $"Ranked: {candidate.Name} PID {candidate.Pid}, {candidate.RecommendedAction}, score {candidate.DangerScore:F1}");
                }
            }

            // Steps 6 & 7: Whitelist Guard → Execution (Tier 3+ only)
            if (_currentTier >= 3 && ranked?.TopCandidate != null && ctx != null && tree != null)
            {
                var candidate = ranked.TopCandidate;
                EscalateTier4Candidate(candidate, decision);

                if (IsDebounced(candidate.Name))
                {
                    // Same process hit recently — count it but don't act yet
                    decision.DecisionReasons.Add($"Debounced: {candidate.Name} (last action {DebounceWindowSeconds}s ago)");
                    IncrementEscalation(candidate.Name);
                }
                else
                {
                    // Step 7 — MUST run before Execution (safety gate, never bypass)
                    var guard = await RunWithTimeoutAsync(
                        () => _whitelistGuardAgent.ApproveActionAsync(tickId, candidate, ctx, tree),
                        "WhitelistGuard", tickId);

                    if (guard != null) decision.ActiveAgents.Add(7);
                    if (guard != null)
                    {
                        _ = _loggerAgent.LogAsync(tickId, "WHITELIST_DECISION", guard);
                    }

                    if (guard?.Decision == "APPROVED")
                    {
                        // Step 6 — Execute (with one retry on failure)
                        var exec = await ExecuteWithRetryAsync(tickId, candidate);

                        if (exec != null)
                        {
                            decision.ActiveAgents.Add(6);
                            decision.ExecutedAction = new OrchestratorDecision.ActionToExecute
                            {
                                TargetPid   = exec.TargetPid,
                                TargetName  = exec.TargetName,
                                ApprovedAction = exec.ActionAttempted,
                                ApprovedBy  = "GUARD"
                            };
                            _lastActionTime = DateTime.UtcNow;
                            _lastActionProcessName = candidate.Name;
                            IncrementEscalation(candidate.Name);
                            _ = _loggerAgent.LogAsync(tickId, "ACTION_TAKEN", exec);
                            decision.DecisionReasons.Add(
                                $"{exec.ActionAttempted} → {exec.TargetName} [{exec.ActionResult}]");
                        }
                    }
                    else
                    {
                        // Guard rejected: downgrade to Warn tier, notify user instead
                        _currentTier = Math.Min(_currentTier, 2);
                        decision.CurrentTier = _currentTier;
                        if (guard != null)
                        {
                            _ = _loggerAgent.LogAsync(tickId, "WHITELIST_BLOCKED", guard);
                        }
                        decision.DecisionReasons.Add($"Guard blocked: {guard?.BlockReason ?? "timeout"} — downgraded to Tier 2");

                        _ = RunWithTimeoutAsync(
                            () => _uiAgent.NotifyAsync(tickId, "WARN_USER", new
                            {
                                reason = guard?.BlockReason,
                                process = candidate.Name
                            }),
                            "UI-Warn", tickId);
                    }
                }
            }
        }

        // ── Step 8: Logger (async, non-blocking) ───────────────────────────
        decision.ActiveAgents.Add(8);
        _ = RunWithTimeoutAsync(
            () => _loggerAgent.LogAsync(tickId, "CYCLE_COMPLETE", decision),
            "Logger", tickId);

        // ── Step 9: UI update (async, non-blocking) ─────────────────────────
        decision.ActiveAgents.Add(9);
        _ = RunWithTimeoutAsync(
            () => _uiAgent.NotifyAsync(tickId, "TRAY_UPDATE", _currentTier),
            "UI", tickId);

        // ── Step 10: Feedback prompt (if action was taken) ──────────────────
        if (decision.ExecutedAction != null)
        {
            decision.ActiveAgents.Add(10);
            _ = ScheduleFeedbackPromptAsync(tickId, decision.ExecutedAction);
        }

        AddToDecisionHistory(decision);
        return decision;
    }

    public int GetCurrentTier() => _currentTier;

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Runs an agent task. Returns null and logs if the agent times out or throws.
    /// This is the graceful-degradation wrapper — the pipeline continues without a failed agent.
    /// </summary>
    private async Task<T?> RunWithTimeoutAsync<T>(Func<Task<T>> operation, string agentName, string tickId)
        where T : class
    {
        try
        {
            var agentTask = operation();
            var winner = await Task.WhenAny(agentTask, Task.Delay(AgentTimeoutMs));

            if (winner == agentTask)
                return await agentTask;

            _ = _loggerAgent.LogAsync(tickId, "AGENT_TIMEOUT", new { agent = agentName });
            return null;
        }
        catch (Exception ex)
        {
            _ = _loggerAgent.LogAsync(tickId, "AGENT_ERROR", new { agent = agentName, error = ex.Message });
            return null;
        }
    }

    /// <summary>
    /// Execute action. If the first attempt fails, retries once before giving up.
    /// </summary>
    private async Task<ExecutionResult?> ExecuteWithRetryAsync(string tickId, RankedAction candidate)
    {
        var exec = await RunWithTimeoutAsync(
            () => _executionAgent.ExecuteActionAsync(tickId, candidate, "GUARD"),
            "Execution", tickId);

        if (exec?.ActionResult == "FAILED")
        {
            _ = _loggerAgent.LogAsync(tickId, "EXECUTION_RETRY",
                new { pid = candidate.Pid, name = candidate.Name });

            exec = await RunWithTimeoutAsync(
                () => _executionAgent.ExecuteActionAsync(tickId, candidate, "GUARD_RETRY"),
                "Execution-Retry", tickId);
        }

        return exec;
    }

    /// <summary>
    /// After an action is taken, wait 30 seconds then prompt the user for feedback via UI.
    /// Runs fire-and-forget so it never blocks the main cycle.
    /// </summary>
    private async Task ScheduleFeedbackPromptAsync(string tickId, OrchestratorDecision.ActionToExecute action)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        _ = RunWithTimeoutAsync(
            () => _uiAgent.NotifyAsync(tickId, "FEEDBACK_REQUEST", action),
            "UI-Feedback", tickId);
    }

    private bool IsMetricsValid(MetricSnapshot m)
    {
        return m.Cpu.OverallPct >= 0
            && !float.IsNaN(m.Cpu.OverallPct)
            && !float.IsInfinity(m.Cpu.OverallPct);
    }

    private int DetermineRuleBasedTier(MetricSnapshot m)
    {
        var worst = Math.Max(m.Cpu.OverallPct, m.Ram.UsagePct);
        if (worst >= 85) return 4;
        if (worst >= 75) return 3;
        if (worst >= 65) return 2;
        return 1;
    }

    private bool IsDebounced(string processName) =>
        processName == _lastActionProcessName &&
        (DateTime.UtcNow - _lastActionTime).TotalSeconds < DebounceWindowSeconds;

    private void IncrementEscalation(string key)
    {
        _escalationCounters.TryGetValue(key, out var count);
        _escalationCounters[key] = count + 1;
    }

    private void EscalateTier4Candidate(RankedAction candidate, OrchestratorDecision decision)
    {
        if (_currentTier < 4)
            return;

        _escalationCounters.TryGetValue(candidate.Name, out var priorActions);
        if (priorActions < 2)
            return;

        if (candidate.RecommendedAction == "THROTTLE")
        {
            candidate.RecommendedAction = "SUSPEND";
            decision.DecisionReasons.Add(
                $"Escalated {candidate.Name}: repeated throttle did not clear Tier 4 pressure; trying SUSPEND.");
        }
    }

    private void AddToMetricsBuffer(MetricSnapshot snapshot)
    {
        _metricsBuffer.Enqueue(snapshot);
        while (_metricsBuffer.Count > MetricsBufferSize)
            _metricsBuffer.Dequeue();
    }

    private void AddToDecisionHistory(OrchestratorDecision decision)
    {
        _decisionHistory.Enqueue(decision);
        while (_decisionHistory.Count > DecisionHistorySize)
            _decisionHistory.Dequeue();
    }
}
