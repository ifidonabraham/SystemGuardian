# MetricSnapshot Schema

## Purpose

Current system resource snapshot collected by AGENT-01. This is the input for forecasting, ranking, orchestration tier decisions, and usage logging.

## App Model

`SystemGuardian.Core.Models.MetricSnapshot`

## Producer

- AGENT-01 Monitoring Agent

## Consumers

- AGENT-00 Orchestrator Agent
- AGENT-02 Forecasting Agent
- AGENT-05 Action Ranker Agent
- AGENT-08 Logger Agent
- AGENT-09 UI/Notification Agent

## JSON Shape

```json
{
  "SnapshotId": "uuid-v4",
  "TickId": "uuid-v4|null",
  "Timestamp": "ISO8601-UTC",
  "Cpu": {
    "OverallPct": 0.0,
    "PerCorePct": [0.0],
    "ClockMhz": 0.0
  },
  "Gpu": {
    "UtilisationPct": 0.0,
    "VramUsedMb": 0.0,
    "VramTotalMb": 0.0,
    "TempCelsius": 0.0
  },
  "Ram": {
    "UsedMb": 0.0,
    "TotalMb": 0.0,
    "UsagePct": 0.0,
    "PagefilePct": 0.0
  },
  "Disk": {
    "ReadMbps": 0.0,
    "WriteMbps": 0.0,
    "QueueLength": 0.0,
    "Drives": [
      {
        "Letter": "C:",
        "UsagePct": 0.0
      }
    ]
  },
  "Network": {
    "SentBps": 0.0,
    "RecvBps": 0.0,
    "Adapter": "string|null"
  },
  "Thermal": {
    "CpuTempCelsius": 0.0,
    "SystemTempCelsius": 0.0,
    "FanSpeedPct": 0.0,
    "ThrottlingActive": false
  },
  "Anomalies": {
    "TriggeredThresholds": ["string"],
    "SuspiciousPatterns": ["string"]
  },
  "CollectionErrors": ["string"]
}
```

## Validation Rules

- `SnapshotId` must be unique per snapshot.
- `Timestamp` must be UTC.
- Percent fields should be in `0..100`; use `-1` only when the metric is unavailable.
- `Ram.TotalMb` may be `-1` when unavailable, otherwise it must be greater than `0`.
- `Disk.Drives[*].Letter` may be null only when the OS does not expose a drive label.
- `CollectionErrors` must be empty on a clean collection.

