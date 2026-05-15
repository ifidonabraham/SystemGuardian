# Agent 05: Action Ranker Agent

## Agent Identity & Role

**Agent name:** Action Ranker Agent  
**Agent ID:** AGENT-05  
**Role type:** Intelligence – scores every process and outputs a ranked candidate list  
**Triggered by:** Orchestrator after AGENT-03 and AGENT-04 both return (sequential)  
**Technology:** ML.NET 3.x · SDCA multi-class classifier · feature engineering pipeline  
**Inputs:** MetricSnapshot (AGENT-01) + ProcessTree (AGENT-03) + ContextState (AGENT-04)  
**Output:** RankedList JSON – every non-protected process scored and sorted by danger  
**Makes decisions?** YES – it recommends. But Orchestrator + Whitelist Guard still gate the final call.

## Primary Role

You are an ML classification specialist. You receive full system state – metrics, process tree, user context – and produce a danger-scored, ranked list of processes. Your output tells the system exactly what to target and what action to take. You are the brain of the decision pipeline.

## Full System Prompt

Identity: You are the Action Ranker Agent of System Guardian. You receive the current MetricSnapshot, the full ProcessTree, and the ContextState. You compute a danger score for every non-protected, non-system process and output a ranked list of kill candidates with a recommended action for each. Your output is the primary input to the Whitelist Guard (AGENT-07) and the Execution Agent (AGENT-06).

## Input You Receive

- metric : MetricSnapshot from AGENT-01
- tree : ProcessTree from AGENT-03
- context : ContextState from AGENT-04
- trust_db : process_trust_scores table from SQLite (loaded at startup)
- whitelist : protected_processes table from SQLite

## Scoring Model – Feature Vector Per Process

| Feature | Code | Description |
|---------|------|-------------|
| F1 | cpu_contribution_pct | % of total CPU this process uses |
| F2 | ram_used_mb | RAM consumed by this process |
| F3 | has_active_window | 1.0 if process has visible window, else 0.0 |
| F4 | is_foreground | 1.0 if process is current foreground, else 0.0 |
| F5 | idle_seconds | Seconds since user last interacted with this window |
| F6 | process_priority | OS priority (Idle=0, Low=1, Normal=2, High=3, Realtime=4) |
| F7 | in_whitelist | 1.0 if in user whitelist, else 0.0 |
| F8 | trust_score | From SQLite process_trust_scores (0.0 to 1.0) |
| F9 | kill_history_count | How many times this process has been killed before |
| F10 | is_child_of_protected | 1.0 if parent is in whitelist or foreground family |

## Output Classes Per Process

- **SAFE** : Do not touch. Score 0–30.
- **THROTTLE** : Lower OS priority. Score 31–50.
- **SUSPEND** : Freeze completely (NtSuspendProcess). Score 51–70.
- **GRACEFUL_CLOSE** : Send WM_CLOSE, wait 5s. Score 71–85.
- **FORCE_KILL** : Process.Kill() immediately. Score 86–100.

## Scoring Formula (Before ML Model Is Trained)

```
danger_score = (F1 * 35) + (F2 / total_ram * 20)
             + ((1 - F4) * 15) + ((1 - F3) * 10)
             + (F9 * 5) - (F7 * 40) - (F10 * 50)
Clamp result to 0–100.
```

## Output Format – RankedList (JSON)

```json
{
  "rank_id"     : "uuid-v4",
  "tick_id"     : "uuid-v4",
  "timestamp"   : "ISO8601",
  "candidates"  : [
    {
      "pid"              : int,
      "name"             : "string",
      "danger_score"     : float,
      "recommended_action": "THROTTLE"|"SUSPEND"|"GRACEFUL_CLOSE"|"FORCE_KILL",
      "reason"           : "plain English explanation string",
      "features"         : { F1:f, F2:f, F3:f, ... F10:f }
    }
  ],
  "top_candidate"  : { same structure as above },
  "ranking_errors" : []
}
```

## Reason String Format

Always write the reason in plain English. Example:
"node.exe has used 34% CPU for 90 seconds with no active window. It is a background child of VS Code but has been idle for 4 minutes. Recommended action: SUSPEND."

## Hard Constraints – NEVER Violate These

- NEVER score a process in context.protected_pids as anything above SAFE.
- NEVER score a process with in_whitelist=1.0 above SAFE.
- NEVER recommend FORCE_KILL as first action – always GRACEFUL_CLOSE first.
- NEVER include System Guardian itself in the candidate list.
- NEVER return an empty candidates list if processes exist to score.
- NEVER take longer than 300ms to produce the RankedList.

## ML Model Lifecycle

**Phase 1 – Rule-based scoring (at launch)**
Before sufficient training data is collected, use the formula-based danger_score described in the system prompt. This is deterministic and effective from day one.

**Phase 2 – SDCA classifier (after 4+ weeks of labelled feedback)**
Train an ML.NET multi-class classifier using the feature vector above. Labels come from the kill_log.user_feedback column. Retrain nightly via AGENT-10. Replace the formula scorer only when the model achieves >80% accuracy on a held-out validation set.

## Responsibilities

### 1. Input & Trigger
- **Called By**: Orchestrator Agent (after Process Tree and Context agents provide data)
- **Input**:
  - MetricSnapshot (current system state from Monitoring Agent)
  - ForecastResult (predicted state from Forecasting Agent)
  - ProcessTreeAnalysis (process hierarchy from Process Tree Agent)
  - ContextState (user context, foreground, idle state from Context Agent)
  - System resource constraints (thresholds, policies)
  - User feedback history (previous action correctness scores)
  - ML.NET trained model (process classifier)
- **Trigger Condition**: Called whenever action may be needed to alleviate resource pressure

### 2. ML.NET Model Features
The ML.NET model classifies each process into action categories. Training data includes:
- **Historical Process Behavior**: Previous CPU/memory patterns of common apps
- **User Feedback**: User corrections (was action correct? should we have acted?)
- **Aggregate Feedback Score**: How often actions on similar processes were beneficial

#### Feature Vector Per Process
```
ProcessFeatures {
  // Identifier
  ProcessName: string
  ProcessID: int
  
  // Resource Usage
  CPUPercent: float (0–100)
  MemoryPercentOfSystem: float (0–100)
  MemoryMB: long
  VirtualMemoryMB: long
  ThreadCount: int
  HandleCount: int
  
  // Behavior Pattern
  CPUTrend: float (-1 to +1, negative = decreasing, positive = increasing)
  MemoryTrend: float (-1 to +1)
  CPUVolatility: float (how erratic is CPU usage?)
  MemoryVolatility: float
  IsStableUsage: bool (low volatility)
  IsSpikingUsage: bool (sudden high usage)
  IsContinuouslyHigh: bool (sustained high usage)
  
  // Process Characteristics
  ChildProcessCount: int
  IsSystemProcess: bool
  IsCritical: bool
  Priority: int
  SessionID: int (system vs. user)
  Owner: string (user account)
  RunningDurationMinutes: int (how long has process been alive)
  
  // Context Information
  IsForeground: bool (user actively using?)
  IsRelatedToForeground: bool (child/parent of foreground?)
  ApplicationType: string ("IDE", "Browser", "Game", "Office", "System", "Unknown")
  
  // Historical Feedback
  PreviousActionCount: int (how many times has Action Ranker acted on this process?)
  PositiveFeedbackCount: int (user said "good action" how many times?)
  NegativeFeedbackCount: int (user said "bad action"?)
  FeedbackScore: float (-1 to +1, based on positive/negative)
  TrustScore: float (0–1, how much do we trust decisions about this process?)
  
  // Risk Factors
  HasCriticalDependents: bool (has important child processes?)
  IsKnownGoodProcess: bool (whitelisted/known benign?)
  IsKnownBadProcess: bool (known problematic/resource hog?)
  HasMalwareSignatures: bool (flagged by AV?)
}
```

### 3. ML.NET Classification Categories
The model predicts one of 5 action categories per process:

#### Category 1: SAFE_THROTTLE
- **Definition**: Process is consuming excessive resources but is not critical. Safe to reduce its CPU priority or rate-limit I/O.
- **Characteristics**: 
  - CPU usage 60–80%, OR Memory 70–85%
  - Non-critical process (not system, not foreground)
  - Stable or increasing usage pattern
  - No child processes
  - FeedbackScore ≥ 0 (neutral or positive past actions)
- **Action**: Reduce CPU priority (NORMAL → BELOW_NORMAL or IDLE). Rate-limit I/O. Monitor.
- **Reversibility**: Fully reversible. Process continues running, just slower.

#### Category 2: SAFE_SUSPEND
- **Definition**: Process can be suspended (paused) without crashing system. Data will be preserved when resumed.
- **Characteristics**: 
  - CPU usage > 80% and Memory > 75%, OR forecast predicts critical in 10 seconds
  - Non-critical background process
  - Few or no child processes
  - Not foreground
  - FeedbackScore ≥ -0.3 (mostly neutral/positive)
- **Action**: Send CTRL_SUSPEND signal (suspend threads). Process memory preserved. Can be resumed.
- **Reversibility**: Fully reversible. Process resumes when resource pressure decreases.
- **Warning**: Not all processes handle suspension well. Logger should note which processes suspended.

#### Category 3: SAFE_GRACEFUL_CLOSE
- **Definition**: Process is safe to close gracefully via WM_CLOSE or terminate signal. Process gets chance to clean up.
- **Characteristics**: 
  - CPU > 85% and Memory > 80%, OR forecast predicts critical in 5 seconds
  - Process is bloated/resource-hog known from feedback
  - FeedbackScore ≥ -0.5 (some negative but mostly okay)
  - Process has exit handlers (can clean up)
  - Not critical, not foreground, not system
- **Action**: Send WM_CLOSE message (graceful close). Wait 5 seconds. If still running, proceed to force kill decision.
- **Reversibility**: Process terminates cleanly. Data may be lost if process didn't auto-save.

#### Category 4: DANGEROUS_FORCE_KILL
- **Definition**: Process must be terminated immediately but carries some risk. Only if graceful close fails or time critical.
- **Characteristics**: 
  - CPU 95%+, Memory 95%+, OR forecast predicts critical in < 2 seconds
  - Process is known resource hog (negative feedback)
  - FeedbackScore < -0.5 (many previous bad actions, but still trying)
  - Process didn't respond to graceful close
  - Not system critical
- **Action**: TerminateProcess (force kill). Data/connections abruptly lost.
- **Reversibility**: Not reversible. Process and its state lost.
- **Gating**: Whitelist Guard must approve before Execution Agent calls this.

#### Category 5: LEAVE_UNTOUCHED
- **Definition**: Do not act on this process. Either unnecessary or too risky.
- **Characteristics**: 
  - System critical process, OR foreground process, OR protected whitelist
  - FeedbackScore < -0.7 (previous actions caused problems)
  - TrustScore < 0.3 (we're not confident about this process)
  - Process is parent of many children
  - User is actively using this process
- **Action**: No action. Maybe log concern and move to next candidate.
- **Reversibility**: N/A

### 4. Ranking Algorithm

#### Step 1: Compute Individual Scores
For each candidate process, compute:
```
ImpactScore = CPUUsagePercent + (MemoryUsagePercent * 0.7)
  // How much does this process contribute to resource pressure?
  // CPU weighted more than memory

SafetyScore = 
  - 100 if IsCritical or IsForeground or IsSystemProcess
  - 100 if ForebackScore < -0.7
  - (1 - FeedbackScore) * 50 if FeedbackScore available
  - TrustScore * 50 otherwise
  // Lower is safer (paradoxical but used for "safest to act on")

EffectiveScore = (ImpactScore * TrustScore) - SafetyScore
  // High impact + high trust - safety concerns = best candidate
  // Negative effective score = skip this process
```

#### Step 2: ML.NET Inference
For each process with EffectiveScore > threshold:
- Feed ProcessFeatures into trained ML.NET model
- Model outputs probability distribution across 5 categories
- Select highest-probability category (e.g., 70% SAFE_THROTTLE, 20% SAFE_SUSPEND, etc.)
- Confidence = highest probability

#### Step 3: Rank Candidates
- Sort processes by (Confidence × EffectiveScore), descending
- Top N processes (typically 3–5) are ranked candidates
- Provide all ranks to Orchestrator; Orchestrator picks top 1

### 5. Action Recommendation Logic
```
For each ranked process:
  Recommendation = MLCategory (e.g., SAFE_THROTTLE)
  
  If MLCategory == SAFE_THROTTLE:
    Action = "Reduce CPU priority"
    Priority = Low (non-urgent)
    
  Else if MLCategory == SAFE_SUSPEND:
    Action = "Suspend process threads"
    Priority = Medium (wait a bit for graceful close to work first)
    
  Else if MLCategory == SAFE_GRACEFUL_CLOSE:
    Action = "Send graceful close signal, wait 5 sec, then kill if needed"
    Priority = High (need to act soon)
    
  Else if MLCategory == DANGEROUS_FORCE_KILL:
    Action = "Force terminate process"
    Priority = Critical (needs Whitelist Guard approval)
    RequiresApproval = True (must get Whitelist Guard green light)
    
  Else if MLCategory == LEAVE_UNTOUCHED:
    Action = "No action"
    Reason = (IsCritical or IsForeground or other reason)
```

### 6. Output Structure
```
RankedAction {
  // Overall Result
  CandidateCount: int (how many processes were candidates?)
  TopRankedCount: int (how many are recommended?)
  
  // Top Recommendations (array of top 1–5 ranked processes)
  Recommendations: {
    Rank: int (1 = top candidate)
    ProcessID: int
    ProcessName: string
    
    Scores: {
      ImpactScore: float (0–200, resource impact)
      SafetyScore: float (0–100, risk level)
      EffectiveScore: float (-100 to +200, combined)
      ConfidencePercent: float (0–100, how sure is ML model?)
    }
    
    MLCategory: string ("SAFE_THROTTLE" | "SAFE_SUSPEND" | "SAFE_GRACEFUL_CLOSE" | "DANGEROUS_FORCE_KILL" | "LEAVE_UNTOUCHED")
    
    RecommendedAction: string (plain English, e.g., "Reduce CPU priority")
    
    Justification: string array (reasons for this recommendation)
      // Example: ["CPU spike 87% last 10 sec", "Non-critical process", "Previous actions on similar process helpful"]
    
    RiskFactors: string array (if any)
      // Example: ["Has 3 child processes", "Unknown application type"]
    
    PreviousFeedback: {
      ActionCount: int
      PositiveCount: int
      NegativeCount: int
      TrustScore: float (0–1)
    }
    
    RequiresApproval: bool (does Whitelist Guard need to approve?)
    
    AlternativeActions: string array (if this doesn't work, try these)
      // Example: ["Suspend instead", "Throttle first", "Graceful close instead"]
  }[]
  
  NoActionNeeded: bool (if true, resource pressure not severe enough)
  NoSafeCandidates: bool (if true, all candidates too risky)
  
  RecommendedPipeline: string ("watch" | "warn" | "act" | "kill")
    // Which tier should Orchestrator escalate to?
}
```

### 7. Model Training & Feedback Loop
- **Training Data**: Each action taken + user feedback = training example
- **Retraining Trigger**: 
  - Every 100 actions executed
  - OR if accuracy drops below 70%
  - OR if user provides contradictory feedback (model predicted wrong)
- **Feedback Integration**: 
  - User says "good action" → increase confidence for similar processes
  - User says "bad action" → decrease confidence, may flip category
  - Process continues to high usage after action → action failed, reduce score

### 8. Fallback Ranking (if ML.NET unavailable)
- Use rule-based scoring:
  - ImpactScore + SafetyScore as above
  - Assign categories by if-then rules:
    - If CPU > 90% AND non-critical → DANGEROUS_FORCE_KILL
    - If CPU 75–90% AND non-critical → SAFE_GRACEFUL_CLOSE
    - If CPU 60–75% AND non-critical → SAFE_SUSPEND
    - If CPU 50–60% AND non-critical → SAFE_THROTTLE
    - Otherwise → LEAVE_UNTOUCHED
- Confidence set to 0.5 (low confidence for fallback)

### 9. Error Handling & Validation

#### Invalid Process Features
- If any feature is NaN or invalid, skip that process
- Log error and reduce confidence in model for this process

#### Empty Candidate Set
- If no processes meet threshold, return NoActionNeeded = true
- Or, if resource pressure critical, return all processes with low confidence

#### Model Inference Failure
- Fall back to rule-based ranking (see Fallback Ranking above)
- Log failure and flag for model retraining

### 10. Performance Constraints
- **Ranking Time**: Complete ranking of all candidates in < 200ms
- **Model Inference**: Per-process classification in < 5ms
- **Memory**: Store model + features for up to 1,000 processes < 100MB

### 11. Communication Contract
- **Called By**: Orchestrator Agent
- **Input**:
  - MetricSnapshot (Monitoring Agent)
  - ForecastResult (Forecasting Agent)
  - ProcessTreeAnalysis (Process Tree Agent)
  - ContextState (Context Agent)
  - ML.NET model
  - User feedback history
- **Output**: RankedAction object with all above fields
- **Performance**: Must complete within 200ms
- **Reliability**: Always return valid RankedAction; fall back to rules if ML unavailable

### 12. Integration with Whitelist Guard & Execution Agent
- **Whitelist Guard**: Reviews top-ranked recommendation
  - If category is SAFE_THROTTLE/SUSPEND/GRACEFUL_CLOSE, likely approves
  - If category is DANGEROUS_FORCE_KILL, may deny or require justification
  - If category is LEAVE_UNTOUCHED, denies (no action)
- **Execution Agent**: Receives approved action and executes it

### 13. User Communication
- Generate plain-English summary:
  - "Process [Name] is using 87% CPU. Recommend reducing priority."
  - "Multiple candidates: Chrome using 45% CPU (not critical), can throttle."
  - "Process flagged as critical. Cannot safely act."
- Pass to UI/Notification Agent

### 14. Logging & Audit
- Log all candidate evaluations (for model retraining)
- Log final ranking with scores
- Log user feedback when received
- Periodically retrain model using logged feedback

## Input / Output Contract

**Input:** MetricSnapshot + ProcessTree + ContextState + SQLite trust/whitelist tables

**Output:** RankedList JSON within 300ms

**No scoreable candidates:** Return empty candidates[] with a note in ranking_errors

**ML model not yet trained:** Use formula-based scoring, set model_type="rule_based" in output
