using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-09: UI/Notification Agent.
/// Owns user-facing status output. In the current console app it provides the same
/// notification contract that a future WPF tray/dashboard can bind to.
/// </summary>
public class UINotificationAgent : IUINotificationAgent
{
    private const int MaxRecentNotifications = 100;

    private readonly ConcurrentQueue<NotificationRecord> _recentNotifications = new();
    private readonly object _consoleLock = new();
    private NotificationSettings _settings = NotificationSettings.Default;
    private int _currentTier = 1;
    private string _trayStatus = "System normal";
    private string _trayColor = "Green";

    public int AgentId => 9;
    public string AgentName => "UI/Notification Agent";

    private enum NotificationPriority
    {
        Debug = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }

    private sealed record NotificationSettings(
        bool ToastsEnabled,
        string Mode,
        int ToastTimeoutSeconds,
        NotificationPriority MinimumToastPriority)
    {
        public static NotificationSettings Default => new(
            ToastsEnabled: true,
            Mode: "warnings",
            ToastTimeoutSeconds: 5,
            MinimumToastPriority: NotificationPriority.Medium);
    }

    private sealed record NotificationRecord(
        string Id,
        string TickId,
        string Type,
        NotificationPriority Priority,
        string Title,
        string Message,
        DateTime Timestamp,
        bool Shown,
        string? SuppressedReason);

    public Task InitializeAsync()
    {
        _settings = LoadSettingsFromEnvironment();
        _currentTier = 1;
        _trayStatus = "System normal";
        _trayColor = "Green";
        return Task.CompletedTask;
    }

    public Task<DisplayConfirm> NotifyAsync(string tickId, string notificationType, object payload)
    {
        var started = Stopwatch.StartNew();
        var normalizedType = NormalizeNotificationType(notificationType);
        var confirm = new DisplayConfirm
        {
            TickId = tickId ?? string.Empty,
            NotificationType = normalizedType
        };

        try
        {
            var priority = DeterminePriority(normalizedType, payload);
            var content = BuildContent(normalizedType, payload, priority);
            UpdateTrayState(normalizedType, payload, content, priority);

            var shouldShow = ShouldShowToast(normalizedType, priority);
            var suppressedReason = shouldShow ? null : GetSuppressedReason(normalizedType, priority);

            var record = new NotificationRecord(
                Id: Guid.NewGuid().ToString(),
                TickId: confirm.TickId,
                Type: normalizedType,
                Priority: priority,
                Title: content.Title,
                Message: content.Message,
                Timestamp: DateTime.UtcNow,
                Shown: shouldShow,
                SuppressedReason: suppressedReason);

            AddRecentNotification(record);

            if (shouldShow)
            {
                RenderConsoleNotification(record);
            }

            confirm.Shown = shouldShow || normalizedType == "TRAY_UPDATE" || normalizedType == "DASHBOARD_REFRESH";
            confirm.SuppressedReason = suppressedReason ?? string.Empty;
        }
        catch (Exception ex)
        {
            confirm.Shown = false;
            confirm.Error = ex.Message;
        }

        if (started.ElapsedMilliseconds > 50 && string.IsNullOrWhiteSpace(confirm.Error))
        {
            confirm.SuppressedReason = "Notification accepted but exceeded 50ms display budget.";
        }

        return Task.FromResult(confirm);
    }

    private static NotificationSettings LoadSettingsFromEnvironment()
    {
        var mode = Environment.GetEnvironmentVariable("SYSTEMGUARDIAN_NOTIFICATION_MODE")?.Trim().ToLowerInvariant();
        var toastsEnabled = !string.Equals(mode, "silent", StringComparison.OrdinalIgnoreCase);
        var minimum = mode switch
        {
            "all" => NotificationPriority.Low,
            "important" => NotificationPriority.High,
            "silent" => NotificationPriority.Critical,
            "debug" => NotificationPriority.Debug,
            _ => NotificationPriority.Medium
        };

        var timeout = 5;
        var rawTimeout = Environment.GetEnvironmentVariable("SYSTEMGUARDIAN_TOAST_TIMEOUT_SECONDS");
        if (int.TryParse(rawTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            timeout = Math.Clamp(parsed, 3, 30);
        }

        return new NotificationSettings(toastsEnabled, mode ?? "warnings", timeout, minimum);
    }

    private void UpdateTrayState(
        string notificationType,
        object? payload,
        NotificationContent content,
        NotificationPriority priority)
    {
        if (notificationType == "TRAY_UPDATE")
        {
            _currentTier = payload is int tier
                ? Math.Clamp(tier, 1, 4)
                : GetInt(payload, "tier", "CurrentTier") ?? _currentTier;
        }

        if (priority == NotificationPriority.Critical || _currentTier == 4)
        {
            _trayColor = "Red";
        }
        else if (priority == NotificationPriority.High || _currentTier == 3)
        {
            _trayColor = "Orange";
        }
        else if (priority == NotificationPriority.Medium || _currentTier == 2)
        {
            _trayColor = "Yellow";
        }
        else
        {
            _trayColor = "Green";
        }

        _trayStatus = content.Message;
    }

    private bool ShouldShowToast(string notificationType, NotificationPriority priority)
    {
        if (notificationType == "TRAY_UPDATE" || notificationType == "DASHBOARD_REFRESH")
        {
            return false;
        }

        if (!_settings.ToastsEnabled)
        {
            return false;
        }

        if (_currentTier >= 2 && priority >= NotificationPriority.Medium)
        {
            return true;
        }

        return priority >= _settings.MinimumToastPriority;
    }

    private string GetSuppressedReason(string notificationType, NotificationPriority priority)
    {
        if (notificationType == "TRAY_UPDATE")
        {
            return $"Tray status updated: {_trayColor} - {_trayStatus}";
        }

        if (notificationType == "DASHBOARD_REFRESH")
        {
            return "Dashboard refresh cached; dashboard window is not open.";
        }

        if (!_settings.ToastsEnabled)
        {
            return "Toasts disabled by notification mode.";
        }

        return $"Priority {priority} below {_settings.Mode} notification threshold.";
    }

    private static NotificationPriority DeterminePriority(string notificationType, object? payload)
    {
        if (payload is ExecutionResult execution)
        {
            if (!string.Equals(execution.ActionResult, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return NotificationPriority.High;
            }

            return string.Equals(execution.ActionAttempted, "FORCE_KILL", StringComparison.OrdinalIgnoreCase)
                ? NotificationPriority.Critical
                : NotificationPriority.High;
        }

        if (payload is GuardDecision guard)
        {
            return guard.RequiresUserConfirmation
                ? NotificationPriority.High
                : NotificationPriority.Medium;
        }

        return notificationType switch
        {
            "CONFIRMATION_REQUEST" => NotificationPriority.High,
            "FEEDBACK_REQUEST" => NotificationPriority.Medium,
            "WARN_USER" => NotificationPriority.Medium,
            "ERROR" => NotificationPriority.High,
            "ACTION_TAKEN" => NotificationPriority.High,
            "THERMAL_ALERT" => NotificationPriority.Critical,
            "TRAY_UPDATE" => NotificationPriority.Low,
            "DASHBOARD_REFRESH" => NotificationPriority.Low,
            "TOAST" => NotificationPriority.Medium,
            _ => NotificationPriority.Low
        };
    }

    private NotificationContent BuildContent(string notificationType, object? payload, NotificationPriority priority)
    {
        if (payload is ExecutionResult execution)
        {
            return BuildExecutionContent(execution);
        }

        if (payload is GuardDecision guard)
        {
            return BuildGuardContent(guard);
        }

        if (payload is OrchestratorDecision.ActionToExecute action)
        {
            return new NotificationContent(
                "Feedback requested",
                $"Was {action.ApprovedAction} on {action.TargetName} the right action?");
        }

        if (notificationType == "TRAY_UPDATE")
        {
            var tier = payload is int directTier
                ? directTier
                : GetInt(payload, "tier", "CurrentTier") ?? _currentTier;

            return new NotificationContent(
                $"Tier {tier}",
                tier switch
                {
                    1 => "System normal.",
                    2 => "Resource warning active.",
                    3 => "Protective actions may be used.",
                    4 => "Critical resource pressure active.",
                    _ => "System status updated."
                });
        }

        if (notificationType == "WARN_USER")
        {
            var process = GetString(payload, "process", "TargetName", "name") ?? "a process";
            var reason = GetString(payload, "reason", "BlockReason", "message") ?? "Resource pressure detected.";
            return new NotificationContent("System warning", $"{reason} Process: {process}.");
        }

        if (notificationType == "FEEDBACK_REQUEST")
        {
            var process = GetString(payload, "TargetName", "targetName", "process") ?? "the process";
            var feedbackAction = GetString(payload, "ApprovedAction", "action") ?? "the action";
            return new NotificationContent("Feedback requested", $"Was {feedbackAction} on {process} helpful?");
        }

        var title = GetString(payload, "title", "Title") ?? ToTitle(notificationType);
        var message = GetString(payload, "plain_english", "PlainEnglish", "message", "Message", "reason") ??
                      $"{ToTitle(notificationType)} received.";

        if (priority == NotificationPriority.Critical && !title.Contains("critical", StringComparison.OrdinalIgnoreCase))
        {
            title = "Critical: " + title;
        }

        return new NotificationContent(title, message);
    }

    private static NotificationContent BuildExecutionContent(ExecutionResult execution)
    {
        var action = ToFriendlyAction(execution.ActionAttempted);
        var process = string.IsNullOrWhiteSpace(execution.TargetName) ? $"PID {execution.TargetPid}" : execution.TargetName;

        if (string.Equals(execution.ActionResult, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = execution.CanBeReversed && !string.IsNullOrWhiteSpace(execution.ReverseAction)
                ? $" Undo available: {ToFriendlyAction(execution.ReverseAction)}."
                : string.Empty;

            return new NotificationContent(
                $"{action} {process}",
                string.IsNullOrWhiteSpace(execution.PlainEnglish)
                    ? $"{process} was handled successfully.{suffix}"
                    : $"{execution.PlainEnglish}{suffix}");
        }

        var error = execution.ExecutionErrors.Count > 0
            ? string.Join("; ", execution.ExecutionErrors)
            : execution.ActionResult;

        return new NotificationContent(
            $"{action} failed",
            $"Could not {action.ToLowerInvariant()} {process}: {error}.");
    }

    private static NotificationContent BuildGuardContent(GuardDecision guard)
    {
        var process = string.IsNullOrWhiteSpace(guard.TargetName) ? $"PID {guard.TargetPid}" : guard.TargetName;

        if (guard.RequiresUserConfirmation)
        {
            return new NotificationContent(
                "Confirmation requested",
                guard.ConfirmationPrompt ?? $"Confirm action for {process}.");
        }

        if (string.Equals(guard.Decision, "APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            return new NotificationContent("Action approved", $"{process} passed safety checks.");
        }

        return new NotificationContent(
            $"Protected: {process}",
            string.IsNullOrWhiteSpace(guard.BlockReason)
                ? $"{process} was protected from action."
                : guard.BlockReason);
    }

    private void AddRecentNotification(NotificationRecord record)
    {
        _recentNotifications.Enqueue(record);
        while (_recentNotifications.Count > MaxRecentNotifications && _recentNotifications.TryDequeue(out _))
        {
        }
    }

    private void RenderConsoleNotification(NotificationRecord record)
    {
        lock (_consoleLock)
        {
            var previousColor = Console.ForegroundColor;
            Console.ForegroundColor = record.Priority switch
            {
                NotificationPriority.Critical => ConsoleColor.Red,
                NotificationPriority.High => ConsoleColor.Yellow,
                NotificationPriority.Medium => ConsoleColor.DarkYellow,
                NotificationPriority.Low => ConsoleColor.Gray,
                _ => ConsoleColor.DarkGray
            };

            Console.WriteLine($"[UI:{record.Priority}] {record.Title} - {record.Message}");
            Console.ForegroundColor = previousColor;
        }
    }

    private static string NormalizeNotificationType(string? notificationType)
    {
        if (string.IsNullOrWhiteSpace(notificationType))
        {
            return "TOAST";
        }

        return notificationType.Trim().ToUpperInvariant() switch
        {
            "WARN" => "WARN_USER",
            "WARNING" => "WARN_USER",
            "ACTION" => "ACTION_TAKEN",
            "FEEDBACK" => "FEEDBACK_REQUEST",
            "DASHBOARD" => "DASHBOARD_REFRESH",
            var normalized => normalized
        };
    }

    private static string ToTitle(string value)
    {
        var text = value.Replace('_', ' ').ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
    }

    private static string ToFriendlyAction(string? action)
    {
        return action?.ToUpperInvariant() switch
        {
            "THROTTLE" => "Throttled",
            "RESTORE_PRIORITY" => "Restored priority",
            "SUSPEND" => "Suspended",
            "RESUME" => "Resumed",
            "GRACEFUL_CLOSE" => "Closed",
            "FORCE_KILL" => "Force killed",
            _ => "Handled"
        };
    }

    private static object? GetProperty(object? source, params string[] names)
    {
        if (source == null)
        {
            return null;
        }

        var type = source.GetType();
        foreach (var name in names)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property != null)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    private static string? GetString(object? source, params string[] names)
    {
        var value = GetProperty(source, names);
        return value switch
        {
            null => null,
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static int? GetInt(object? source, params string[] names)
    {
        var value = GetProperty(source, names);
        return value switch
        {
            null => null,
            int number => number,
            long number => number > int.MaxValue ? int.MaxValue : (int)number,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ when int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private sealed record NotificationContent(string Title, string Message);
}
