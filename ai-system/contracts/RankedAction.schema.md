# RankedAction Schema

## Purpose

Ranked process-action recommendations produced by AGENT-05. The top candidate may be passed to AGENT-07 for approval before AGENT-06 can execute anything.

## App Models

- `SystemGuardian.Core.Models.RankedList`
- `SystemGuardian.Core.Models.RankedAction`

## Producer

- AGENT-05 Action Ranker Agent

## Consumers

- AGENT-00 Orchestrator Agent
- AGENT-07 Whitelist Guard Agent
- AGENT-08 Logger Agent
- AGENT-09 UI/Notification Agent

## RankedList JSON Shape

```json
{
  "RankId": "uuid-v4",
  "TickId": "uuid-v4",
  "Timestamp": "ISO8601-UTC",
  "Candidates": [
    {
      "Pid": 1234,
      "Name": "example.exe",
      "DangerScore": 72.5,
      "RecommendedAction": "SAFE|THROTTLE|SUSPEND|GRACEFUL_CLOSE|FORCE_KILL",
      "Reason": "string",
      "Features": {
        "cpu_pct": 90.0,
        "ram_pct": 40.0,
        "is_foreground": 0.0,
        "is_system": 0.0,
        "has_window": 1.0
      }
    }
  ],
  "TopCandidate": null,
  "RankingErrors": ["string"]
}
```

## RankedAction JSON Shape

```json
{
  "Pid": 1234,
  "Name": "example.exe",
  "DangerScore": 72.5,
  "RecommendedAction": "SAFE|THROTTLE|SUSPEND|GRACEFUL_CLOSE|FORCE_KILL",
  "Reason": "string",
  "Features": {
    "feature_name": 0.0
  }
}
```

## Validation Rules

- `DangerScore` must be in `0..100`.
- `RecommendedAction = FORCE_KILL` must not be emitted as a first action for a process.
- `TopCandidate` must be null when no safe candidate exists.
- `TopCandidate` must be one of `Candidates` when present.
- `Features` values must be finite numbers.
- `RankingErrors` must be empty on a clean ranking pass.

