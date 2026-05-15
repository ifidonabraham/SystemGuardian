# ForecastResult Schema

## Purpose

Thirty-second forward resource forecast produced by AGENT-02 from the rolling metric buffer. The orchestrator uses this contract to choose the active tier.

## App Model

`SystemGuardian.Core.Models.ForecastResult`

## Producer

- AGENT-02 Forecasting Agent

## Consumers

- AGENT-00 Orchestrator Agent
- AGENT-05 Action Ranker Agent
- AGENT-08 Logger Agent
- AGENT-09 UI/Notification Agent

## JSON Shape

```json
{
  "ForecastId": "uuid-v4",
  "TickId": "uuid-v4",
  "Timestamp": "ISO8601-UTC",
  "Resources": {
    "cpu": {
      "CurrentPct": 0.0,
      "ProjectedPct": 0.0,
      "Trend": "rising|falling|stable|unknown",
      "Confidence": 0.0,
      "WillBreach": false,
      "BreachEtaSec": null
    },
    "ram": {
      "CurrentPct": 0.0,
      "ProjectedPct": 0.0,
      "Trend": "rising|falling|stable|unknown",
      "Confidence": 0.0,
      "WillBreach": false,
      "BreachEtaSec": null
    },
    "gpu": {
      "CurrentPct": 0.0,
      "ProjectedPct": 0.0,
      "Trend": "rising|falling|stable|unknown",
      "Confidence": 0.0,
      "WillBreach": false,
      "BreachEtaSec": null
    },
    "disk": {
      "CurrentPct": 0.0,
      "ProjectedPct": 0.0,
      "Trend": "rising|falling|stable|unknown",
      "Confidence": 0.0,
      "WillBreach": false,
      "BreachEtaSec": null
    },
    "network": {
      "CurrentPct": 0.0,
      "ProjectedPct": 0.0,
      "Trend": "rising|falling|stable|unknown",
      "Confidence": 0.0,
      "WillBreach": false,
      "BreachEtaSec": null
    }
  },
  "WorstResource": "cpu|ram|gpu|disk|network|unknown",
  "RecommendedTier": 1,
  "BufferSizeUsed": 0,
  "ModelErrors": ["string"]
}
```

## Validation Rules

- `RecommendedTier` must be `1`, `2`, `3`, or `4`.
- `Confidence` must be in `0..1`.
- `CurrentPct` and `ProjectedPct` should be in `0..100`; use `-1` only when unavailable.
- `BreachEtaSec` must be null when `WillBreach` is false.
- `WorstResource` must match a key in `Resources` unless forecasting failed.
- `ModelErrors` must be empty on a clean forecast.

