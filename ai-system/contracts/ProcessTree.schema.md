# ProcessTree Schema

## Purpose

Complete process hierarchy built by AGENT-03. It gives downstream agents parent-child relationships, safe action ordering, and system-process protections.

## App Model

`SystemGuardian.Core.Models.ProcessTree`

## Producer

- AGENT-03 Process Tree Agent

## Consumers

- AGENT-04 Context Agent
- AGENT-05 Action Ranker Agent
- AGENT-06 Execution Agent
- AGENT-07 Whitelist Guard Agent
- AGENT-08 Logger Agent

## JSON Shape

```json
{
  "TreeId": "uuid-v4",
  "TickId": "uuid-v4",
  "Timestamp": "ISO8601-UTC",
  "TotalProcesses": 0,
  "Nodes": [
    {
      "Pid": 1234,
      "ParentPid": 1000,
      "Name": "example.exe",
      "Children": [2345],
      "Depth": 1,
      "IsSystem": false,
      "WindowTitle": "string|null",
      "SessionId": 1,
      "SafeKillOrder": [2345, 1234]
    }
  ],
  "BuildErrors": ["string"]
}
```

## Validation Rules

- `TreeId` must be unique per tree build.
- `TotalProcesses` should equal `Nodes.Count`.
- `Pid` must be greater than `0`.
- `ParentPid` must be `0` when no parent is known.
- `Children` must contain PIDs present in `Nodes` when those processes are visible to the collector.
- `Depth` must be `0` for roots and increase by parent-child level.
- `SafeKillOrder` must place children before parents.
- `IsSystem = true` means the process must not be killed by AGENT-06.
- `BuildErrors` must be empty on a complete build.

