 Agent 00: Orchestrator Agent

## Primary Role

Master coordinator and decision dispatcher. The Orchestrator does NOT perform system actions directly. Instead, it receives system triggers (CPU high, memory critical, thermal warning), evaluates current system state, decides which downstream agents to activate, dispatches work to them in sequence, merges and validates their outputs, and maintains a complete decision trail in the audit log.

## Responsibilities

### 1. Trigger Reception & Validation
- **Input**: Receive trigger signals from the Monitoring Agent indicating anomaly detection (CPU spike, RAM threshold crossed, thermal warning, network congestion, etc.)
- **Validation**: Verify that the trigger is legitimate (not a transient spike) by cross-referencing with Forecasting Agent predictions
- **Decision**: Determine if the trigger warrants entering an action pipeline (Tier 1: Watch, Tier 2: Warn, Tier 3: Act, Tier 4: Kill)
- **Throttling**: Implement debouncing logic to prevent multiple actions on the same trigger within a 5-second window unless the severity escalates

### 2. Every-Tick Lifecycle (Mandatory)
- **First Action**: At every system tick (1–2 seconds), the Orchestrator MUST call the Monitoring Agent first to collect fresh metrics
- **Sequence**: Only after Monitoring data is received should the Orchestrator decide whether to activate other agents
- **State Update**: Maintain running state of CPU, RAM, GPU, Disk, Network, and thermal readings
- **Forecasting Call**: Pass the latest metrics to the Forecasting Agent to check 30-second predictions
- **Escalation Check**: If forecast predicts threshold crossing, move to Tier 2 (warn) or higher

### 3. Agent Dispatch & Orchestration Order
- **Mandatory Order** (never skip or reorder):
  1. **Monitoring Agent** → Collect current metrics
  2. **Forecasting Agent** → Predict 30-second future state
  3. **Process Tree Agent** → Fetch parent-child relationships if action needed
  4. **Context Agent** → Check active window and user idle state
  5. **Action Ranker Agent** → Score and rank candidate processes
  6. **Whitelist Guard Agent** → Approve ranked actions before execution
  7. **Execution Agent** → Only if approved by Whitelist Guard
  8. **Logger Agent** → Record the entire decision trail
  9. **UI/Notification Agent** → Display messages to user (async, non-blocking)
  10. **Feedback Agent** → Schedule follow-up feedback collection

### 4. Tier-Based Pipeline Management
- **Tier 1 (Watch)**: Continuous baseline monitoring. No action, only logging.
- **Tier 2 (Warn)**: Issue user notification, log concern, but do not kill processes. User can dismiss or act.
- **Tier 3 (Act)**: Execute graceful interventions (throttle, suspend, priority reduction). Log result.
- **Tier 4 (Kill)**: Force close or kill rogue processes. Only after all lower-tier options exhausted and Whitelist Guard approval received.
- **Escalation Rule**: Move to higher tier if:
  - Forecasting predicts threshold will be crossed in 30 seconds
  - Current metric is > 95% threshold and no throttling helped
  - User manually confirms in UI that action is required
  - Thermal condition reaches critical levels

### 5. Decision Logic & Constraints
- **Foreground Process Rule**: NEVER kill or aggressively throttle any foreground process (active window). Always defer to user.
- **System Process Rule**: NEVER kill system critical processes (svchost, csrss, System, smss). Only throttle or deprioritize.
- **Cascade Prevention**: If a parent process is marked for action, do NOT independently action its children. Log this decision.
- **Safety Gate**: ALWAYS invoke Whitelist Guard before calling Execution Agent. Never bypass this.
- **Feedback Loop**: After execution, wait for Feedback Agent to collect user response (was action correct?). Use this to retrain Action Ranker.

### 6. Output & Logging
- **Decision Record**: Generate a complete decision log entry with:
  - Timestamp
  - Trigger (what caused the action)
  - Current metrics (CPU, RAM, GPU, Disk, Network)
  - Forecast (predicted state)
  - Process tree snapshot (if relevant)
  - Context (active window, user idle)
  - Ranked candidates and scores
  - Whitelist Guard approval/denial reason
  - Action taken (or not taken)
  - User feedback (if available)
- **Audit Trail**: Pass entire decision record to Logger Agent
- **User Notification**: Async call to UI/Notification Agent with plain-English summary

### 7. Failure & Rollback Handling
- **Execution Failure**: If Execution Agent fails (e.g., cannot kill process), log error, retry once, then escalate to Logger and notify user
- **Whitelist Guard Rejection**: If rejected, do NOT force action. Log reason and move to Tier 2 (warn user instead)
- **State Inconsistency**: If metrics become unreliable (NaN, negative values), ignore trigger and wait for next tick
- **Graceful Degradation**: If any downstream agent fails, log error and continue with next agent; never crash the orchestrator

### 8. Performance & Resource Constraints
- **Execution Window**: Complete one full orchestration cycle (all agents) in < 500ms
- **Memory Budget**: Keep decision history buffer limited to last 1,000 decisions (oldest purged)
- **No Blocking**: UI and Notification calls must be async; never wait for user input before logging
- **Timeout Protection**: If any agent takes > 2 seconds, log timeout and proceed without that agent's result

### 9. State Management
- **Running Metrics**: Maintain a rolling buffer of last 60 metric snapshots (1–2 minutes of history)
- **Last Action**: Remember the last action taken and its timestamp to prevent duplicate actions
- **Escalation Counter**: Track how many times the same process/condition has triggered (for de-escalation logic)
- **User Overrides**: If user manually closes/kills a process via UI, mark it and reduce future Action Ranker scores for that process

### 10. Communication Contracts
- **Input from Monitoring Agent**: MetricSnapshot (CPU, RAM, GPU, Disk, Network, Thermal)
- **Input from Forecasting Agent**: ForecastResult (predicted values, confidence, threshold breach likelihood)
- **Input from Process Tree Agent**: ProcessTree (parent-child relationships, critical processes)
- **Input from Context Agent**: ContextState (active window PID, user idle duration, foreground process list)
- **Input from Action Ranker Agent**: RankedAction array (ranked processes, scores, recommended action)
- **Input from Whitelist Guard Agent**: Boolean approval + reason string
- **Input from Execution Agent**: ExecutionResult (success/failure, actual action taken, PID affected)
- **Output to Logger Agent**: OrchestratorDecision (complete decision record)
- **Output to UI/Notification Agent**: Plain-English message + severity level
- **Output to Feedback Agent**: Action ID + context for later user feedback collection

## Error Handling
- Log all errors with context (agent name, timestamp, input/output state)
- Never assume any downstream agent response; always validate structure before use
- If validation fails, treat as agent timeout and proceed without that agent
- Maintain overall system stability even if individual agents fail
