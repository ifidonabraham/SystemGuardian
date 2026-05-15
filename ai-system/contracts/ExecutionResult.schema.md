# ExecutionResult Schema

## Purpose

Outcome of an approved process-control action performed by AGENT-06. This is the durable audit input for AGENT-08 and the basis for later feedback.

## App Model

`SystemGuardian.Core.Models.ExecutionResult`

## Producer

- AGENT-06 Execution Agent

## Consumers

- AGENT-00 Orchestrator Agent
- AGENT-08 Logger Agent
- AGENT-09 UI/Notification Agent
- AGENT-10 Feedback Agent

## JSON Shape

```json
{
  "ExecutionId": "uuid-v4",
  "TickId": "uuid-v4",
  "Timestamp": "ISO8601-UTC",
  "TargetPid": 1234,
  "TargetName": "example.exe",
  "ActionAttempted": "THROTTLE|RESTORE_PRIORITY|SUSPEND|RESUME|GRACEFUL_CLOSE|FORCE_KILL",
  "ActionResult": "SUCCESS|FAILED|TIMEOUT|ACCESS_DENIED|BLOCKED|NOT_SUPPORTED",
  "EscalatedTo": "string|null",
  "ActionMethod": "SetPriorityClass|NtSuspendProcess|NtResumeProcess|WM_CLOSE|Kill|None",
  "DurationMs": 0.0,
  "ProcessAliveAfter": true,
  "CanBeReversed": false,
  "ReverseAction": "RESTORE_PRIORITY|RESUME|null",
  "PlainEnglish": "string",
  "ExecutionErrors": ["string"]
}
```

## Validation Rules

- `ExecutionId` must be unique per execution attempt.
- `TargetPid` must be greater than `0`.
- `DurationMs` must be non-negative.
- `CanBeReversed` must be true only when `ReverseAction` is non-null.
- `FORCE_KILL` must never be used as an escalation from `GRACEFUL_CLOSE` inside the same result.
- `ActionResult = SUCCESS` must include a human-readable `PlainEnglish` summary.
- `ExecutionErrors` must be empty when `ActionResult = SUCCESS`.

