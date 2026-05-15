# OrchestratorDecision Schema

## Purpose

Master decision record produced by AGENT-00 after each orchestration cycle. It summarizes the tier, participating agents, decision reasons, and any approved action that was dispatched.

## App Model

`SystemGuardian.Core.Models.OrchestratorDecision`

## Producer

- AGENT-00 Orchestrator Agent

## Consumers

- AGENT-08 Logger Agent
- AGENT-09 UI/Notification Agent
- AGENT-10 Feedback Agent

## JSON Shape

```json
{
  "DecisionId": "uuid-v4",
  "TickId": "uuid-v4",
  "Timestamp": "ISO8601-UTC",
  "CurrentTier": 1,
  "PreviousTier": 1,
  "TierChanged": false,
  "ActiveAgents": [1, 2, 8, 9],
  "WorstResource": "cpu|ram|gpu|disk|network|unknown",
  "WorstResourcePct": 0.0,
  "ExecutedAction": {
    "TargetPid": 1234,
    "TargetName": "example.exe",
    "ApprovedAction": "THROTTLE|SUSPEND|GRACEFUL_CLOSE|FORCE_KILL",
    "ApprovedBy": "GUARD|GUARD_RETRY|USER"
  },
  "ResumeAction": false,
  "PauseReason": "string",
  "DecisionReasons": ["string"]
}
```

## Validation Rules

- `DecisionId` must be unique per cycle.
- `CurrentTier` and `PreviousTier` must be `1`, `2`, `3`, or `4`.
- `TierChanged` must equal `CurrentTier != PreviousTier`.
- `ActiveAgents` must contain agent IDs that ran or were scheduled during the cycle.
- `WorstResourcePct` should be in `0..100`; use `0` when there is no resource pressure.
- `ExecutedAction` must be null unless AGENT-07 approved and AGENT-06 was dispatched.
- `DecisionReasons` must contain at least one readable reason for the decision.

