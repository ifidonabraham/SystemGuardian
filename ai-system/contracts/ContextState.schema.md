# ContextState Schema

## Purpose

Current user and foreground-window context produced by AGENT-04. This contract prevents actions against active, protected, or recently used processes.

## App Model

`SystemGuardian.Core.Models.ContextState`

## Producer

- AGENT-04 Context Agent

## Consumers

- AGENT-05 Action Ranker Agent
- AGENT-07 Whitelist Guard Agent
- AGENT-08 Logger Agent
- AGENT-09 UI/Notification Agent

## JSON Shape

```json
{
  "ContextId": "uuid-v4",
  "TickId": "uuid-v4",
  "Timestamp": "ISO8601-UTC",
  "ForegroundPid": 1234,
  "ForegroundName": "example.exe",
  "ForegroundPath": "C:\\Path\\example.exe|null",
  "ForegroundWindowTitle": "string|null",
  "ForegroundWindowClass": "string|null",
  "ForegroundWindowVisible": true,
  "ForegroundWindowMinimized": false,
  "ForegroundWindowMaximized": false,
  "UserIdleSeconds": 0.0,
  "UserIsIdle": false,
  "IdleLevel": "ACTIVE|SEMI_ACTIVE|IDLE|VERY_IDLE",
  "LastInputUtc": "ISO8601-UTC|null",
  "ProtectedPids": [1234],
  "RecentlyActivePids": [1234],
  "ForegroundAppType": "Browser|Editor|Terminal|Game|Media|System|Other",
  "IsForegroundProtected": true,
  "ProtectionReason": "string",
  "CurrentUser": "string",
  "SessionId": 1,
  "SessionType": "interactive|service|locked|unknown",
  "CanThrottleProcesses": false,
  "CanSuspendProcesses": false,
  "CanKillProcesses": false,
  "RecommendedContextAction": "notify user|observe|allow low-risk action|defer action",
  "UserAlertLevel": "none|info|warning|critical",
  "PlainEnglishSummary": "string",
  "ContextErrors": ["string"]
}
```

## Validation Rules

- `ContextId` must be unique per context snapshot.
- `ForegroundPid` may be `0` only when no foreground window is available.
- `UserIdleSeconds` must be non-negative.
- `UserIsIdle` must be consistent with `IdleLevel`.
- `ProtectedPids` must include `ForegroundPid` when `IsForegroundProtected` is true and `ForegroundPid > 0`.
- `CanKillProcesses` must be false when the foreground process is protected or the session is not interactive.
- `ContextErrors` must be empty on a clean context read.

