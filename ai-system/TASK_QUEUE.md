Architecture Breakdown
Layer 1 — Monitoring Engine (C# + .NET)

Use PerformanceCounter or System.Diagnostics for CPU/RAM
Use LibreHardwareMonitor (open source NuGet) for GPU stats
Use WMI queries for Disk and Network I/O
Poll every 1–2 seconds, store rolling averages

Layer 2 — ML Decision Engine
This is the brain. Two approaches to consider:
ApproachWhat it doesComplexityRule-based thresholdKill if CPU > 85% for 10sEasy, but dumbML.NET anomaly detectionDetects unusual spikes before they peakMediumTrained classifierLearns which processes are "safe to kill"Hard but powerful
My recommendation: start with rules + anomaly detection, then layer the classifier on top as V2.
Layer 3 — Process Intelligence

Rank every running process by a "danger score": high CPU + low priority + no active window = kill candidate
Whitelist system-critical processes (svchost, explorer, lsass) — never touch these
Differentiate between foreground (user is using it) vs background processes
For VS Code specifically: use Windows API to send WM_CLOSE to its window handle — graceful shutdown first, then force kill if it doesn't respond in 5 seconds

Layer 4 — SQLite Preferences Store
Store things like:

Protected apps (never kill these)
Kill history (what was killed, when, why)
User-defined thresholds per resource
Process trust scores (learned over time)

Layer 5 — WPF UI

A live dashboard (real-time graphs for CPU/GPU/RAM/Disk)
A process table showing danger scores
Alert notifications before any action is taken
A settings panel tied to SQLite preferences


The "Never Hit 100%" Strategy
This is the most important design decision. The system should work in 3 tiers:
75% usage → WARN:  Show notification, user can act
85% usage → ACT:   Auto-throttle background processes (lower priority)
92% usage → KILL:  Terminate the highest-danger non-essential process
Never wait until 100% — that's already too late. You want to intercept at 85%.

The ML Model — What to Train On
The model should classify each process as one of:

SAFE — leave it alone
THROTTLE — reduce its CPU priority
KILL — terminate it

Features to feed the model per process:

CPU % used (rolling avg)
RAM consumed
Has active window? (yes/no)
Is it in the user's protected list?
Time since last user interaction with it
Process priority level
Historical kill frequency