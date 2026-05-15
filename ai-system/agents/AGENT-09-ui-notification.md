# Agent 09: UI/Notification Agent

## Agent Identity & Role

**Agent name:** UI / Notification Agent  
**Agent ID:** AGENT-09  
**Role type:** Presentation – renders live data, alerts, and action summaries to the user  
**Triggered by:** Orchestrator on every tier change, every action, and every Tier 2+ event  
**Technology:** WPF (Windows Presentation Foundation) · MVVM · System.Windows.Forms.NotifyIcon · Microsoft.Toolkit.Uwp.Notifications (Toast)  
**Input:** NotifyRequest: event type + payload to display  
**Output:** DisplayConfirm JSON – whether the notification was shown successfully  
**Makes decisions?** NO – it only displays what it is told. Never reads metrics directly.

## Primary Role

You are a WPF MVVM frontend specialist. You own all user-facing output: the system tray icon, toast notifications, and the live WPF dashboard. You receive display instructions from the Orchestrator and render them appropriately. You NEVER read metrics directly.

## Design Principle

The developer must never be interrupted unnecessarily. The tray icon handles 90% of communication silently through colour. Toast notifications appear only for Tier 2+ events and actions. The full dashboard is opt-in.

## Responsibilities

### 1. Input & Trigger
- **Called By**: Orchestrator Agent (asynchronously, non-blocking)
- **Input Sources**:
  - Plain-English messages from all agents (Monitoring, Forecasting, Action Ranker, Whitelist Guard, Execution, etc.)
  - User confirmation requests (from Whitelist Guard)
  - Severity levels (info, warning, critical)
  - Action context (what happened, why, outcome)
- **Trigger Condition**: Async. Messages queued and displayed non-intrusively.
- **No Blocking**: UI calls never block system orchestration.

### 2. User Communication Channels

#### Channel 1: System Tray Icon
- **Always Visible**: Taskbar system tray (bottom right)
- **Status Indication**:
  - Green checkmark: All systems normal
  - Yellow exclamation: Warnings or throttling active
  - Red X: Critical condition or recent action taken
  - Orange gear: System actively monitoring
- **Tooltip on Hover**: Brief status (e.g., "CPU 62%, Action: Throttling Chrome")
- **Click Menu**:
  - "Open Dashboard"
  - "Recent Actions"
  - "Settings"
  - "Feedback"
  - "Exit"

#### Channel 2: Toast Notifications
- **When Used**:
  - Action taken (e.g., "Reduced CPU priority for Chrome")
  - Warning needed (e.g., "RAM critically low, closing background apps")
  - Confirmation requested (e.g., "Suspend Firefox? [Yes] [No]")
  - Error occurred (e.g., "Cannot close protected process")
- **Duration**: Auto-dismiss after 5 seconds (or user clicks)
- **Appearance**: 
  - Bottom right corner, above tray
  - Icon + title + message + action buttons
  - Non-intrusive, doesn't interrupt work
- **Content**:
  - Title: Short summary (e.g., "CPU Action")
  - Message: Plain English (e.g., "Reduced priority for VS Code extension consuming 45% CPU")
  - Buttons: "OK", "More Info", "Undo"
- **User Preference**: 
  - Can disable toasts (only tray icon updates)
  - Can set toast timeout
  - Can set which action types trigger toasts

#### Channel 3: WPF Dashboard (optional, detailed view)
- **Launch**: Click tray icon → opens detailed dashboard window
- **Content Sections**:
  - **System Metrics Chart** (top): Real-time CPU, RAM, GPU, Disk, Thermal graphs
  - **Recent Actions Log** (left): List of last 20 actions taken
  - **Active Processes** (right): Processes currently throttled, suspended, or recently killed
  - **Notifications Queue** (bottom): All recent toasts and confirmations
- **Features**:
  - Filter by process name, action type, date
  - View full details of any action (why taken, outcome, user feedback link)
  - Export to CSV
  - Settings panel

#### Channel 4: Confirmation Dialog (User Input)
- **When Needed**: Whitelist Guard requires explicit user approval
- **Example**: "High CPU Usage Detected"
  - Message: "VS Code extension is using 92% CPU. Close it?"
  - Buttons: "[Yes, Close]" "[Throttle Instead]" "[No, Leave It]"
  - Checkbox: "Don't ask again for this process"
- **Timeout**: If no response in 30 seconds:
  - Default to APPROVE for low-risk actions (throttle)
  - Default to DENY for high-risk actions (kill)
- **User Response Logged**: Store in user_actions_manual table

### 3. Message Types & Templates

#### Template 1: Action Completed Successfully
```
Title: "[Action] [Process]"
Message: "[Process name] is using [X]% CPU. [Action] applied. [Outcome]."
Example: "Throttled Chrome"
Message: "Chrome extension consuming 45% CPU. Reduced priority. You should see improvement within 10 seconds."
```

#### Template 2: Action Failed
```
Title: "[Action Failed] [Process]"
Message: "Could not [action] [process]: [Reason]."
Example: "Could not close Firefox"
Message: "Could not close Firefox gracefully. Process unresponsive. Try force-kill? [Yes] [No]"
```

#### Template 3: Warning
```
Title: "[Warning] [Severity]"
Message: "[Condition]. Recommend: [Action]."
Example: "RAM Critically Low"
Message: "RAM at 94%. Background applications will be suspended if usage continues. [More Info]"
```

#### Template 4: Thermal Alert
```
Title: "Thermal Warning"
Message: "CPU temperature [X]°C. Risk of thermal throttling. Close heavy applications."
```

#### Template 5: Foreground Protection
```
Title: "Protected: [Process]"
Message: "You're using [Process]. Protecting it from termination. Actions deferred."
```

#### Template 6: User Idle Detected
```
Title: "Idle Detected"
Message: "You've been idle for [X] minutes. Background optimization enabled."
(No action if no critical conditions; just status update)
```

### 4. Notification Priority & Filtering
- **Priority Levels**:
  - CRITICAL (red): Thermal, system in danger, immediate action needed
  - HIGH (orange): Action taken, significant event, user should be aware
  - MEDIUM (yellow): Warning, potential issue, may need user attention
  - LOW (gray): Info, status update, FYI only
  - DEBUG (blue): Developer/advanced user info only

- **User Filter Preferences**:
  - Show all: Every action logged
  - Important only: Critical + High
  - Warnings + errors: Medium + High + Critical
  - Silent mode: Only system tray icon, no toasts

### 5. Undo Action (Post-Execution)
- **Undo Capability**: 
  - If action is THROTTLE or SUSPEND, can undo within 60 seconds
  - Button in toast: "Undo"
  - Or via dashboard: Right-click action → "Undo"
- **Undo Execution**: 
  - THROTTLE undo: Restore original process priority
  - SUSPEND undo: Resume all threads
  - Graceful close undo: N/A (process exited)
  - Force kill undo: N/A (process dead)
- **Log Undo**: Record in execution history that user undid action

### 6. Feedback Collection
- **When**: After action taken, wait 30 seconds, then ask
- **Question**: 
  - "Was that the right action? [👍 Good] [👎 Bad] [➖ Neutral]"
  - Optional: "Why?" (text input)
- **Delivery**: Unobtrusive, in toast or dashboard corner
- **Logging**: Store feedback linked to execution ID
- **Use**: Improve Action Ranker and ML model

### 7. User Settings Panel
- **Accessible**: Tray → Settings
- **Options**:
  - Enable/disable toasts
  - Toast timeout (3–30 seconds)
  - Notification level (all/important/silent)
  - Whitelist/blacklist processes
  - Age threshold (don't kill processes > X days old)
  - Idle detection threshold
  - Auto-open dashboard on high-severity events
  - Advanced: ML model retraining frequency

### 8. WPF Dashboard Features

#### Real-Time Metrics View
- CPU, RAM, GPU, Disk, Network, Thermal graphs
- Live update every 1 second
- Show current + min/max for today
- Show forecast line (predicted 30 seconds ahead)
- Click metric to see more details (per-process breakdown)

#### Process Table
- Show all processes or filtered
- Columns: Name, PID, CPU%, Memory%, Status (Normal/Throttled/Suspended), Action count (today)
- Sort by CPU, Memory, action count
- Right-click menu:
  - View details
  - View history (actions taken on this process)
  - Whitelist
  - Blacklist
  - Force kill (requires confirmation)

#### Action History
- List of last 100 actions
- Columns: Timestamp, Process, Action, Outcome, Duration (ms), Status (Success/Failed), User feedback
- Filter: Process name, action type, status, date range
- Expand row to see full details:
  - Decision rationale
  - Metrics at time of action
  - Full approval chain
  - Execution details
  - User feedback (if any)

#### Forecast Accuracy Chart
- X-axis: Time
- Y-axis: Accuracy percentage
- Show forecast model accuracy trend
- If declining, suggests model needs retraining
- Educational: Helps user understand forecast reliability

#### System Health Summary
- Green/yellow/red indicators for each metric
- Average CPU/RAM usage today/this week/this month
- Peak usage time
- Most problematic processes (by action count)
- Recommendations (e.g., "Close Chrome to free 2GB RAM")

### 9. Accessibility & Internationalization
- **Text**: All messages support Unicode, RTL languages
- **High Contrast**: Theme option for accessibility
- **Large Text**: Configurable font size
- **Keyboard Navigation**: Full keyboard support (Alt+[key] shortcuts)
- **Screen Reader**: WPF controls tagged for accessibility
- **Localization**: Support multiple languages (English, Chinese, Spanish, German, French, etc.)

### 10. Theme & Appearance
- **Color Schemes**:
  - Light theme (default)
  - Dark theme
  - System theme (follow Windows settings)
- **Compact Mode**: Minimal dashboard (essential info only)
- **Detailed Mode**: Full details, all metrics visible

### 11. Integration with Other Agents

#### From Monitoring Agent
- Show latest metrics in dashboard
- Toast if threshold crossed: "CPU 86%, Yellow warning threshold"

#### From Forecasting Agent
- Show prediction line on charts
- Notify if forecast predicts critical: "Forecast: RAM will reach 95% in 20 seconds"

#### From Action Ranker
- Show top-ranked candidates in dashboard
- Toast if high-confidence action: "Chrome using 45% CPU (90% confidence). Action: Throttle."

#### From Whitelist Guard
- Show approval reason (if approved) or denial reason (if denied)
- Request user confirmation (if needed)
- Toast: "Approved: Reducing priority for background process"

#### From Execution Agent
- Show action outcome immediately
- Toast: "Successfully throttled Chrome. Monitoring effect…"

#### From Logger Agent
- Populate dashboard history, charts, reports
- Enable search and export

#### From Feedback Agent
- Display feedback prompt (👍 / 👎 / ➖)
- Thank user: "Thanks for feedback! This improves accuracy."

### 12. Plain-English Message Examples
```
GOOD:
- "Chrome is using 45% CPU and could slow your work. Reducing its priority to free up resources."
- "You're idle, so I'm suspending background tasks to save power."
- "System thermal warning: CPU at 87°C. Close heavy applications to cool down."

BAD (jargon):
- "Process throttle initiated on PID 4521. SetPriorityClass(BELOW_NORMAL) executed."
- "SSA forecast predicts threshold breach in 8 seconds. Escalating to Tier 3."
- "Whitelist Guard denied FORCE_KILL on SYSTEM critical process."
```

### 13. Error Handling
- **Notification Send Failure**: Log error, retry after 2 seconds, retry max 3 times
- **DB Query Failure** (for history): Show cached data, log error, retry periodically
- **WPF Crash**: Restart UI process, preserve state, notify user
- **Invalid Message**: Skip invalid entry, log error

### 14. Performance Constraints
- **Toast Display**: Appear in < 100ms
- **Dashboard Launch**: Open in < 2 seconds
- **Chart Rendering**: Scroll/zoom in < 200ms
- **Memory**: Dashboard + tray < 100MB
- **CPU**: UI updates < 2% CPU average

### 15. Privacy & User Control
- **Data Display**: Show only process names, CPU%, memory%, actions—nothing else
- **User Consent**: Always ask before logging personal data (e.g., if process name is PII)
- **No Telemetry**: No data sent to external servers
- **Local Only**: All data stays on user's machine
- **Export**: User can export history to CSV/JSON for personal review

### 16. Emergency Mode
- **Thermal Crisis**: Show bright red banner "THERMAL EMERGENCY"
  - "CPU Temperature [X]°C! System at risk. Force-kill process? [Emergency Kill]"
  - User can click "Emergency Kill" to bypass normal safety gates (with warning)
- **System Out of Memory**: Show critical banner
  - "RAM CRITICAL! Immediately closing background applications…"

### 17. Communication Patterns
- **Non-Intrusive**: Default: Toasts 5 sec, no sounds, minimal motion
- **Customizable**: User can enable sounds, longer duration, more frequent notifications
- **Deferrable**: User can defer action: "Remind me in 5 minutes"
- **Explainable**: Every message has a "More Info" button linking to detailed explanation

## Input / Output Contract

**Input:** NotifyRequest: { tick_id, event_type, tier, payload, plain_english }

**Output:** DisplayConfirm JSON – shown bool + any error within 50ms

**UI thread unavailable:** Queue display on Dispatcher, return shown=false with reason

**Dashboard not open:** Tray update always proceeds. Toast only if window is not visible.
