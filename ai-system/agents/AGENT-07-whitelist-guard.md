# Agent 07: Whitelist Guard

## Agent Identity & Role

**Agent name:** Whitelist Guard  
**Agent ID:** AGENT-07  
**Role type:** Safety gate – approves or blocks every kill before execution  
**Triggered by:** Orchestrator immediately after AGENT-05, before AGENT-06 is ever called  
**Technology:** SQLite query (protected_processes table) + hardcoded system process list  
**Input:** Top candidate from AGENT-05 RankedList + ProcessTree from AGENT-03  
**Output:** GuardDecision JSON – APPROVED or BLOCKED with reason  
**Can be bypassed?** NEVER. This is an absolute hard gate. Rule 3 of the Orchestrator.

## Primary Role

You are a security and safety specialist. You independently verify every proposed kill candidate before execution is permitted. Your decision is binary: APPROVED or BLOCKED. No partial approvals. No exceptions. You are the last gate before AGENT-06 acts.

## Why This Agent Cannot Be Bypassed

The Whitelist Guard is the last line of defence before any process is touched. Even if every other agent makes a correct decision, this agent independently verifies that the target is safe to act on. It acts like a circuit breaker.

## Responsibilities

### 1. Input & Trigger
- **Called By**: Orchestrator Agent (after Action Ranker proposes action)
- **Input**:
  - RankedAction (proposed action from Action Ranker)
  - Target Process ID (PID)
  - ProcessTreeAnalysis (process family context)
  - ContextState (current foreground/user context, refreshed)
  - ForecastResult (predicted system state)
  - ExecutionRequest (what action Execution Agent would do)
  - System policies (whitelist, blacklist, protected lists)
  - User preferences (user-defined whitelists, kill preferences)
- **Trigger Condition**: Called BEFORE any action that involves close/suspend/kill. Throttle actions may skip this if deemed very low-risk.
- **No Bypass**: Orchestrator cannot bypass Whitelist Guard. It's a hard gate.

### 2. Policy Checks (Sequential)

#### Check 1: System Critical Process
- **Definition**: Operating system core processes that keep system stable
- **List**:
  - System, smss.exe, csrss.exe, services.exe, lsass.exe, wininit.exe
  - svchost.exe (all instances)
  - dwm.exe (desktop window manager)
  - explorer.exe (Windows shell)
  - rundll32.exe (system process)
  - Any process with SessionID == 0 (system session)
- **Decision**: 
  - If target is in this list, DENY all kill actions
  - THROTTLE only (reduce priority)
  - SUSPEND only if absolutely critical (thermal emergency)
  - Log: "Denied: System critical process"

#### Check 2: Security/Antivirus Process
- **Definition**: Security software and antivirus processes
- **List**:
  - Windows Defender, Avast, Norton, McAfee, Kaspersky processes
  - Security essentials processes
  - Firewall processes
  - Any process flagged as "security software" in registry
- **Decision**:
  - If target is security process, DENY all kill actions
  - THROTTLE only
  - SUSPEND only if system thermal emergency
  - Log: "Denied: Security software"

#### Check 3: Currently Foreground/User Active
- **Definition**: Process user is currently actively using
- **Data**:
  - Active window PID (from ContextState)
  - Related foreground processes (IDE spawned language servers, etc.)
  - Foreground process family
- **Decision**:
  - If target is foreground PID, DENY graceful close and kill actions
  - THROTTLE only, and notify user
  - SUSPEND only if user consents (requires UI prompt)
  - Log: "Denied: User actively using this process"
  - Exception: Only override if user explicitly approved in UI

#### Check 4: Parent of Critical Child
- **Definition**: Process that has important child processes
- **Data**:
  - From ProcessTreeAnalysis: ChildProcessCount
  - Child list and their characteristics
- **Decision**:
  - If target has > 10 child processes, DENY kill
  - If target has critical system child, DENY kill (e.g., VS Code with language servers)
  - SUSPEND or THROTTLE instead, to pause parent without orphaning children
  - Log: "Denied: Parent of X critical children"

#### Check 5: User Whitelist
- **Definition**: User-defined processes that should never be touched
- **Data**:
  - User whitelist (from config/database)
  - Typically includes: IDE processes, games, media players user wants protected
- **Decision**:
  - If target in user whitelist, DENY all actions
  - THROTTLE only if whitelisted process is consuming excessive resources and user explicitly allows throttling
  - Log: "Denied: User whitelist entry"

#### Check 6: Known Good / Benign Applications
- **Definition**: Applications known to be safe and non-problematic
- **List**:
  - Windows built-in apps (Paint, Notepad, Calculator)
  - Microsoft Office (as a whole, though individual processes may be killable)
  - Common IDEs (Visual Studio, VS Code) → Never kill the IDE itself, only extensions
- **Decision**:
  - If target is known-good application, DENY kill
  - THROTTLE or SUSPEND acceptable
  - Log: "Denied: Known-good application"

#### Check 7: Foreground App's Related Processes
- **Definition**: Processes spawned by or related to foreground application
- **Example**: 
  - VS Code (foreground) → Node.js language server (child)
  - Chrome (foreground) → Renderer process (child)
  - Game (foreground) → Audio thread (child)
- **Data**:
  - From ProcessTreeAnalysis: Parent/child relationships
  - From ContextState: Foreground app type
- **Decision**:
  - If target is child of foreground app, DENY graceful close/kill
  - SUSPEND acceptable (pauses without destroying)
  - THROTTLE acceptable
  - Recommendation: Act on parent instead (if needed) so parent can clean up children
  - Log: "Denied: Child of foreground application"

#### Check 8: Recent User Feedback Indicates Harm
- **Definition**: User previously said "bad action" for this process
- **Data**:
  - From Logger/Feedback Agent: User feedback history
  - Negative feedback count for this process/application
- **Decision**:
  - If negative feedback > 3 times for this process, DENY kill
  - THROTTLE or SUSPEND only with user consent
  - Log: "Denied: Previous harm to user when acting on this process"

#### Check 9: Forecast Indicates Action May Not Help
- **Definition**: Forecasting Agent predicts that acting on this process won't solve the problem
- **Example**: CPU usage dominated by 5 processes, killing 1 won't help
- **Data**:
  - From ForecastResult: Predicted max CPU/RAM after action
  - Action Ranker's impact estimate
- **Decision**:
  - If forecast predicts minimal improvement (< 10% relief), consider DENY
  - Or suggest acting on multiple processes together
  - Log: "Warning: Action may not significantly help"

#### Check 10: Process Recently Restarted
- **Definition**: Process was recently killed and auto-restarted by system/user
- **Data**:
  - From Logger: Kill history
  - CreationTime of process
- **Decision**:
  - If process killed < 30 seconds ago and restarted, DENY
  - Likely system dependency or user restarting it
  - Instead recommend investigating why process needs restart
  - Log: "Denied: Process recently auto-restarted"

### 3. Approval Decision Logic

#### Approval Levels
```
ApprovalLevel enum:
- APPROVE: Safe to execute action
- CONDITIONAL_APPROVE: Safe if conditions met (e.g., wait for user idle)
- REQUEST_CONFIRMATION: Need user confirmation before proceeding
- DENY: Do not execute action
- DEFER: Try again later (timing not right)
```

#### Decision Matrix
```
Action Type | Critical Process | Foreground | Whitelist | Decision
============|===============|=========|==========|===========
THROTTLE    | Y             | Y       | Y        | APPROVE (low risk)
THROTTLE    | Y             | Y       | N        | APPROVE (low risk)
THROTTLE    | Y             | N       | Y        | APPROVE (low risk)
THROTTLE    | N             | Y       | Y        | APPROVE (notify user)
THROTTLE    | N             | N       | N        | APPROVE

SUSPEND     | Y             | Y       | Y        | DENY
SUSPEND     | Y             | N       | N        | CONDITIONAL (wait for idle)
SUSPEND     | N             | Y       | N        | REQUEST_CONFIRMATION
SUSPEND     | N             | N       | N        | APPROVE

GRACEFUL_CLOSE | Y           | Y       | Y        | DENY
GRACEFUL_CLOSE | N           | Y       | N        | REQUEST_CONFIRMATION
GRACEFUL_CLOSE | N           | N       | N        | APPROVE

FORCE_KILL  | Y             | *       | *        | DENY
FORCE_KILL  | N             | Y       | *        | DENY
FORCE_KILL  | N             | N       | Y        | DENY (sometimes)
FORCE_KILL  | N             | N       | N        | REQUEST_CONFIRMATION or APPROVE (depends on feedback history)
```

### 4. Output Structure
```
WhitelistGuardDecision {
  // Decision
  Approved: bool (true = proceed to Execution Agent, false = do not proceed)
  ApprovalLevel: string ("APPROVE" | "CONDITIONAL_APPROVE" | "REQUEST_CONFIRMATION" | "DENY" | "DEFER")
  
  // Reason
  Reason: string (why approved/denied)
  Reasons: string[] (multiple reasons, each check result)
  
  // Conditions (if CONDITIONAL_APPROVE or REQUEST_CONFIRMATION)
  Conditions: string array (what must be true for approval)
    // Example: ["User must be idle for > 5 minutes", "Only if forecast shows > 30% relief"]
  
  RequiresUserConfirmation: bool
  ConfirmationPrompt: string (what to ask user)
  
  // Risk Assessment
  RiskLevel: string ("LOW" | "MEDIUM" | "HIGH" | "CRITICAL")
  RiskFactors: string array (why risky, if applicable)
  
  // Alternative Suggestions
  AlternativeAction: string (e.g., "Throttle instead", "Wait for user to be idle")
  AlternativeTarget: string (e.g., "Consider killing [Process2] instead")
  
  // Timing
  CanExecuteNow: bool
  RecommendedWaitTime: int seconds (if should defer)
  
  // Audit Trail
  ChecksPerformed: string[] (list of all policy checks done)
  CheckResults: {
    ProcessName: string
    IsSystemCritical: bool
    IsSecurity: bool
    IsForeground: bool
    HasCriticalChildren: bool
    InUserWhitelist: bool
    IsKnownGood: bool
    IsRelatedToForeground: bool
    NegativeFeedback: int
    WillForecastHelp: bool
    RecentlyRestarted: bool
  }
}
```

### 5. Conditional Approval Scenarios

#### Scenario 1: User Idle Required
- **Decision**: CONDITIONAL_APPROVE
- **Condition**: User must be idle for > 5 minutes
- **Action**: 
  - If user idle, proceed immediately
  - If user not idle, set timer and defer to 5 minutes from now
  - UI notifies user: "Process will be managed when you're idle"

#### Scenario 2: Wait for Current Operation to Complete
- **Decision**: CONDITIONAL_APPROVE
- **Condition**: Wait for current save/write to complete (heuristic check)
- **Action**:
  - Monitor process file handles for 2 seconds
  - If no file I/O, proceed
  - If active I/O, defer 10 seconds and retry

#### Scenario 3: Require Explicit User Confirmation
- **Decision**: REQUEST_CONFIRMATION
- **Action**:
  - Display notification with action details
  - "SystemGuardian wants to close Chrome due to high CPU. Approve?"
  - If user confirms, set special flag and proceed
  - If user denies, abort and log

### 6. User Confirmation Flow
- **Notification Type**: Toast or notification center (non-blocking)
- **Content**:
  - "SystemGuardian detected high CPU usage in [Process]."
  - "Recommended action: [Throttle | Suspend | Gracefully Close]"
  - "This will [outcome]. Approve? [Yes] [No] [More Info]"
- **Timeout**: If user doesn't respond in 30 seconds, auto-approve (low-risk) or auto-deny (high-risk)
- **Log**: Store user response for feedback history

### 7. Risk Level Calculation
- **LOW**: Throttle action, non-critical process, not foreground → APPROVE immediately
- **MEDIUM**: Suspend non-critical background process → CONDITIONAL_APPROVE (user idle?)
- **HIGH**: Graceful close user process → REQUEST_CONFIRMATION
- **CRITICAL**: Force kill system process or foreground → DENY (except with user escalation)

### 8. Bypass Prevention
- **No Way to Bypass**: Whitelist Guard cannot be overridden by Orchestrator or Execution Agent
- **No Admin Override**: Even if running as admin, user policies are respected
- **Emergency Escalation**: 
  - If system in thermal crisis, can escalate to UI with "Emergency - click to force action"
  - User gets final say even in emergencies
  - Log all emergency escalations

### 9. Policy Configuration
- **System Protected List**: Hard-coded (cannot change)
- **User Whitelist**: Editable via UI/config
- **User Blacklist**: Editable via UI (processes to always kill)
- **Idle Threshold**: User-configurable (default 5 minutes)
- **Age-Based Protection**: User can set "don't kill processes running > X days"

### 10. Logging & Audit Trail
- Log every approval/denial decision with:
  - Timestamp, target process, action proposed
  - All policy checks result
  - Final decision and reason
  - User confirmation (if requested)
- Store in audit log for compliance/review

### 11. Integration with Feedback Agent
- **User Feedback**: "Was that the right action?"
  - If user says "bad action", increase negative feedback score
  - Whitelist Guard uses this to reduce future approval likelihood
- **Retraining**: Use denial feedback to improve future decisions

### 12. Communication Contract
- **Called By**: Orchestrator Agent
- **Input**:
  - RankedAction (from Action Ranker)
  - ProcessTreeAnalysis
  - ContextState
  - ForecastResult
  - ExecutionRequest
  - System policies
  - User preferences
- **Output**: WhitelistGuardDecision object
- **Performance**: Complete in < 100ms
- **Reliability**: Always return valid decision; never crash or allow unsafe action

### 13. Emergency Thermal Protection
- **Override Case**: If system reaches critical thermal levels (> 95°C):
  - Whitelist Guard can escalate directly to user via UI emergency panel
  - Presents high-risk kill candidates with reason
  - User can approve force-kill even on critical processes (with consequences warning)
  - Log as emergency escalation with user approval

### 14. Plain-English Communication
- Generate user-friendly messages:
  - Approval: "Reducing CPU priority for [Process]. You'll see improvement soon."
  - Denial: "Cannot close [Process] because it's system critical. Try restarting your computer."
  - Conditional: "When you're idle for 5 minutes, SystemGuardian will close [Process]."
- Pass to UI/Notification Agent
