using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-07: Whitelist Guard.
/// Independent hard safety gate before AGENT-06 can mutate any process.
/// </summary>
public class WhitelistGuardAgent : IWhitelistGuardAgent
{
    public int AgentId => 7;
    public string AgentName => "Whitelist Guard Agent";

    private const float ActionThreshold = 12f;
    private const int IdleForKillSeconds = 300;
    private const int MaxSafeChildCountForClose = 10;

    private readonly HashSet<string> _userWhitelist = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _negativeFeedback = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SystemCriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "rundll32", "spoolsv", "MsMpEng", "audiodg", "fontdrvhost",
        "SearchIndexer", "WmiPrvSE"
    };

    private static readonly HashSet<string> SecurityProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "Sense",
        "avastsvc", "avastui", "avgsvc", "avgui", "nortonsecurity", "nswscsvc",
        "mcshield", "mfemms", "mfevtps", "mbamservice", "mbamtray", "avp", "kavfs",
        "bdagent", "vsserv", "wrsa"
    };

    private static readonly HashSet<string> KnownGoodProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "mspaint", "calculator", "calc", "winword", "excel", "powerpnt", "onenote",
        "Code", "Code - Insiders", "devenv", "rider64", "rider", "chrome", "msedge", "firefox",
        "Teams", "ms-teams", "slack", "discord", "zoom", "outlook"
    };

    public Task InitializeAsync()
    {
        // Future: load protected_processes and feedback summaries from SQLite.
        return Task.CompletedTask;
    }

    public async Task<GuardDecision> ApproveActionAsync(
        string tickId,
        RankedAction candidate,
        ContextState context,
        ProcessTree tree)
    {
        var sw = Stopwatch.StartNew();
        var decision = new GuardDecision
        {
            TickId = tickId,
            TargetPid = candidate.Pid,
            TargetName = candidate.Name
        };

        try
        {
            var action = NormalizeAction(candidate.RecommendedAction);
            var processName = NormalizeProcessName(candidate.Name);
            var node = tree.Nodes.FirstOrDefault(n => n.Pid == candidate.Pid);

            decision.RiskLevel = CalculateInitialRisk(action);

            if (!ValidateCandidate(candidate, action, decision))
                return Complete(decision, sw);

            if (IsProcessGone(candidate.Pid))
                return Approve(decision, "LOW", "PROCESS_ALREADY_EXITED",
                    $"{candidate.Name} is already gone; execution can record no action needed.", sw);

            if (BlockIfSystemCritical(candidate, processName, node, action, decision))
                return Complete(decision, sw);

            if (BlockIfSecurityProcess(candidate, processName, action, decision))
                return Complete(decision, sw);

            if (BlockIfForeground(candidate, context, action, decision))
                return Complete(decision, sw);

            if (BlockIfProtectedPid(candidate, context, action, decision))
                return Complete(decision, sw);

            if (BlockIfUserWhitelisted(candidate, processName, action, decision))
                return Complete(decision, sw);

            if (BlockIfKnownGood(candidate, processName, action, decision))
                return Complete(decision, sw);

            if (BlockIfForegroundRelated(candidate, context, node, action, decision))
                return Complete(decision, sw);

            if (BlockIfParentRisk(candidate, tree, node, action, decision))
                return Complete(decision, sw);

            if (BlockIfFeedbackIndicatesHarm(candidate, processName, action, decision))
                return Complete(decision, sw);

            if (BlockIfRecentlyRestarted(candidate, action, decision))
                return Complete(decision, sw);

            if (BlockOrDeferByAction(candidate, context, action, node, decision))
                return Complete(decision, sw);

            return Approve(decision, CalculateApprovalRisk(action, node),
                "ALL_CHECKS_PASSED",
                $"Approved {action} for {candidate.Name} (PID {candidate.Pid}); all safety checks passed.",
                sw);
        }
        catch (Exception ex)
        {
            Block(decision, "GUARD_ERROR", $"Whitelist Guard failed safely: {ex.Message}", "CRITICAL");
            return Complete(decision, sw);
        }
    }

    private static bool ValidateCandidate(RankedAction candidate, string action, GuardDecision decision)
    {
        decision.ChecksPerformed.Add("VALIDATE_CANDIDATE");

        if (candidate.Pid <= 0 || string.IsNullOrWhiteSpace(candidate.Name))
        {
            Block(decision, "INVALID_TARGET", "Blocked: target PID or name is invalid.", "HIGH");
            return false;
        }

        if (string.IsNullOrWhiteSpace(action) || action == "SAFE")
        {
            Block(decision, "NO_ACTION", "Blocked: no executable action was proposed.", "LOW");
            return false;
        }

        if (candidate.DangerScore < ActionThreshold)
        {
            Block(decision, "LOW_DANGER_SCORE",
                $"Blocked: danger score {candidate.DangerScore:F0} is below action threshold {ActionThreshold:F0}.", "LOW");
            return false;
        }

        return true;
    }

    private static bool BlockIfSystemCritical(
        RankedAction candidate,
        string processName,
        ProcessTree.ProcessNode? node,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("SYSTEM_CRITICAL");

        if (SystemCriticalProcesses.Contains(processName) || node?.IsSystem == true || node?.SessionId == 0)
        {
            Block(decision, "SYSTEM_CRITICAL",
                $"Blocked: {candidate.Name} is system-critical or runs in session 0.", "CRITICAL");
            decision.AlternativeAction = action == "THROTTLE" ? "Notify user" : "THROTTLE";
            return true;
        }

        return false;
    }

    private static bool BlockIfSecurityProcess(
        RankedAction candidate,
        string processName,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("SECURITY_PROCESS");

        if (SecurityProcesses.Contains(processName))
        {
            Block(decision, "SECURITY_SOFTWARE",
                $"Blocked: {candidate.Name} appears to be security or antivirus software.", "CRITICAL");
            decision.AlternativeAction = action == "THROTTLE" ? "Notify user" : "THROTTLE";
            return true;
        }

        return false;
    }

    private static bool BlockIfForeground(
        RankedAction candidate,
        ContextState context,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("FOREGROUND_PROCESS");

        if (candidate.Pid != context.ForegroundPid)
            return false;

        if (action == "THROTTLE")
        {
            decision.RiskFactors.Add("Target is foreground; throttle is low risk but user should be notified.");
            return false;
        }

        Block(decision, "IS_FOREGROUND",
            $"Blocked: {candidate.Name} is the active foreground process.", "CRITICAL");
        decision.RequiresUserConfirmation = action is "SUSPEND" or "GRACEFUL_CLOSE";
        decision.ConfirmationPrompt = $"SystemGuardian wants to {action.ToLowerInvariant()} the foreground app {candidate.Name}.";
        decision.AlternativeAction = "THROTTLE";
        return true;
    }

    private static bool BlockIfProtectedPid(
        RankedAction candidate,
        ContextState context,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("PROTECTED_PID");

        if (!context.ProtectedPids.Contains(candidate.Pid))
            return false;

        if (action == "THROTTLE")
        {
            decision.RiskFactors.Add("Target is protected by context; throttle allowed as lowest-risk action.");
            return false;
        }

        Block(decision, "PROTECTED_PID",
            $"Blocked: {candidate.Name} belongs to the foreground or recently active protected process family.", "HIGH");
        decision.AlternativeAction = "THROTTLE";
        return true;
    }

    private bool BlockIfUserWhitelisted(
        RankedAction candidate,
        string processName,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("USER_WHITELIST");

        if (!_userWhitelist.Contains(processName))
            return false;

        Block(decision, "USER_WHITELIST",
            $"Blocked: {candidate.Name} is in the user whitelist.", "HIGH");
        decision.AlternativeAction = action == "THROTTLE" ? "Notify user" : "THROTTLE";
        return true;
    }

    private static bool BlockIfKnownGood(
        RankedAction candidate,
        string processName,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("KNOWN_GOOD");

        if (!KnownGoodProcesses.Contains(processName))
            return false;

        if (action is "GRACEFUL_CLOSE" or "FORCE_KILL")
        {
            Block(decision, "KNOWN_GOOD_APP",
                $"Blocked: {candidate.Name} is a known-good user application; destructive actions need user confirmation.", "HIGH");
            decision.RequiresUserConfirmation = true;
            decision.ConfirmationPrompt = $"Close {candidate.Name} to relieve resource pressure?";
            decision.AlternativeAction = "THROTTLE";
            return true;
        }

        decision.RiskFactors.Add($"{candidate.Name} is known-good; only reversible action is allowed.");
        return false;
    }

    private static bool BlockIfForegroundRelated(
        RankedAction candidate,
        ContextState context,
        ProcessTree.ProcessNode? node,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("FOREGROUND_RELATED");

        bool related = node != null &&
            (context.ProtectedPids.Contains(node.ParentPid) ||
             node.Children.Any(context.ProtectedPids.Contains));

        if (!related)
            return false;

        if (action is "THROTTLE" or "SUSPEND")
        {
            decision.RiskFactors.Add("Target is related to foreground app; only reversible action allowed.");
            return false;
        }

        Block(decision, "FOREGROUND_RELATED",
            $"Blocked: {candidate.Name} is related to the foreground app family.", "HIGH");
        decision.AlternativeAction = "SUSPEND";
        return true;
    }

    private static bool BlockIfParentRisk(
        RankedAction candidate,
        ProcessTree tree,
        ProcessTree.ProcessNode? node,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("PARENT_CHILD_RISK");

        if (node == null || node.Children.Count == 0)
            return false;

        bool hasSystemChild = node.Children
            .Select(childPid => tree.Nodes.FirstOrDefault(n => n.Pid == childPid))
            .Any(child => child?.IsSystem == true);

        if ((node.Children.Count > MaxSafeChildCountForClose || hasSystemChild) &&
            action is "GRACEFUL_CLOSE" or "FORCE_KILL")
        {
            Block(decision, "CRITICAL_CHILDREN",
                $"Blocked: {candidate.Name} has {node.Children.Count} child process(es), including critical or high-risk children.", "HIGH");
            decision.AlternativeAction = "SUSPEND";
            return true;
        }

        if (node.Children.Count > 0)
            decision.RiskFactors.Add($"Target has {node.Children.Count} child process(es).");

        return false;
    }

    private bool BlockIfFeedbackIndicatesHarm(
        RankedAction candidate,
        string processName,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("NEGATIVE_FEEDBACK");

        int count = _negativeFeedback.GetValueOrDefault(processName);
        if (count <= 3)
            return false;

        if (action is "GRACEFUL_CLOSE" or "FORCE_KILL")
        {
            Block(decision, "NEGATIVE_FEEDBACK",
                $"Blocked: prior feedback indicates acting on {candidate.Name} harmed the user {count} times.", "HIGH");
            decision.AlternativeAction = "THROTTLE";
            return true;
        }

        decision.RiskFactors.Add($"Prior negative feedback count for {candidate.Name}: {count}.");
        return false;
    }

    private static bool BlockIfRecentlyRestarted(
        RankedAction candidate,
        string action,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("RECENTLY_RESTARTED");

        try
        {
            using var process = Process.GetProcessById(candidate.Pid);
            if ((DateTime.Now - process.StartTime).TotalSeconds < 30 &&
                action is "GRACEFUL_CLOSE" or "FORCE_KILL")
            {
                Block(decision, "RECENTLY_RESTARTED",
                    $"Blocked: {candidate.Name} started less than 30 seconds ago and may be auto-restarting.", "MEDIUM");
                decision.RecommendedWaitSeconds = 30;
                decision.AlternativeAction = "Wait and re-evaluate";
                return true;
            }
        }
        catch
        {
            // If the process has gone away, later execution will no-op successfully.
        }

        return false;
    }

    private static bool BlockOrDeferByAction(
        RankedAction candidate,
        ContextState context,
        string action,
        ProcessTree.ProcessNode? node,
        GuardDecision decision)
    {
        decision.ChecksPerformed.Add("ACTION_POLICY");

        switch (action)
        {
            case "THROTTLE":
                return false;

            case "SUSPEND":
                if (!context.CanSuspendProcesses)
                {
                    var isForeground = candidate.Features.GetValueOrDefault("F4_is_foreground") >= 1f;
                    if (!isForeground && candidate.DangerScore >= 30f)
                    {
                        decision.RiskFactors.Add(
                            "Suspend allowed under severe pressure because the target is not foreground and the action is reversible.");
                        return false;
                    }

                    Block(decision, "USER_NOT_IDLE",
                        $"Blocked: user is {context.IdleLevel}; suspend requires idle context.", "MEDIUM");
                    decision.ApprovalLevel = "DEFER";
                    decision.RecommendedWaitSeconds = Math.Max(0, 120 - (int)context.UserIdleSeconds);
                    decision.AlternativeAction = "THROTTLE";
                    return true;
                }
                return false;

            case "GRACEFUL_CLOSE":
                if (!context.CanKillProcesses)
                {
                    Block(decision, "USER_NOT_IDLE_FOR_CLOSE",
                        "Blocked: graceful close requires the user to be idle long enough to reduce data-loss risk.", "HIGH");
                    decision.RequiresUserConfirmation = true;
                    decision.ConfirmationPrompt = $"Close {candidate.Name} to relieve resource pressure?";
                    decision.AlternativeAction = context.CanSuspendProcesses ? "SUSPEND" : "THROTTLE";
                    return true;
                }
                return false;

            case "FORCE_KILL":
                Block(decision, "FORCE_KILL_REQUIRES_CONFIRMATION",
                    "Blocked: force kill is destructive and requires explicit user confirmation in this build.", "CRITICAL");
                decision.RequiresUserConfirmation = true;
                decision.ConfirmationPrompt = $"Force kill {candidate.Name}? Unsaved data may be lost.";
                decision.AlternativeAction = node?.Children.Count > 0 ? "SUSPEND" : "GRACEFUL_CLOSE";
                return true;

            default:
                Block(decision, "UNSUPPORTED_ACTION", $"Blocked: unsupported action {action}.", "HIGH");
                return true;
        }
    }

    private static GuardDecision Approve(
        GuardDecision decision,
        string riskLevel,
        string rule,
        string reason,
        Stopwatch sw)
    {
        decision.Decision = "APPROVED";
        decision.ApprovalLevel = "APPROVE";
        decision.BlockReason = reason;
        decision.BlockRuleTriggered = rule;
        decision.RiskLevel = riskLevel;
        decision.CanExecuteNow = true;
        decision.CheckDurationMs = sw.Elapsed.TotalMilliseconds;
        return decision;
    }

    private static void Block(GuardDecision decision, string rule, string reason, string riskLevel)
    {
        decision.Decision = "BLOCKED";
        decision.ApprovalLevel = decision.ApprovalLevel == "DEFER" ? "DEFER" :
            decision.RequiresUserConfirmation ? "REQUEST_CONFIRMATION" : "DENY";
        decision.BlockReason = reason;
        decision.BlockRuleTriggered = rule;
        decision.RiskLevel = riskLevel;
        decision.CanExecuteNow = false;
        decision.RiskFactors.Add(reason);
    }

    private static GuardDecision Complete(GuardDecision decision, Stopwatch sw)
    {
        if (decision.RequiresUserConfirmation && decision.ApprovalLevel != "DEFER")
            decision.ApprovalLevel = "REQUEST_CONFIRMATION";
        else if (decision.Decision == "BLOCKED" && string.IsNullOrWhiteSpace(decision.ApprovalLevel))
            decision.ApprovalLevel = "DENY";

        decision.CheckDurationMs = sw.Elapsed.TotalMilliseconds;
        return decision;
    }

    private static string NormalizeAction(string action)
    {
        return action?.Trim().ToUpperInvariant() ?? string.Empty;
    }

    private static string NormalizeProcessName(string processName)
    {
        var normalized = processName?.Trim() ?? string.Empty;
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static bool IsProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CalculateInitialRisk(string action)
    {
        return action switch
        {
            "THROTTLE" => "LOW",
            "SUSPEND" => "MEDIUM",
            "GRACEFUL_CLOSE" => "HIGH",
            "FORCE_KILL" => "CRITICAL",
            _ => "HIGH"
        };
    }

    private static string CalculateApprovalRisk(string action, ProcessTree.ProcessNode? node)
    {
        if (action == "THROTTLE") return "LOW";
        if (action == "SUSPEND") return node?.Children.Count > 0 ? "MEDIUM" : "LOW";
        if (action == "GRACEFUL_CLOSE") return "HIGH";
        return "CRITICAL";
    }
}
