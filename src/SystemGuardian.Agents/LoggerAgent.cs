using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-08: Logger Agent - durable append-only audit trail backed by SQLite.
/// The logger never influences decisions; it only records what happened.
/// </summary>
public class LoggerAgent : ILoggerAgent
{
    public int AgentId => 8;
    public string AgentName => "Logger Agent";

    private const int MaxQueueSize = 10_000;
    private readonly ConcurrentQueue<PendingLog> _pendingLogs = new();
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private int _pendingCount;
    private bool _initialized;
    private string _connectionString = string.Empty;

    private sealed record PendingLog(string TickId, string LogType, object? Payload, DateTime QueuedAt);

    public async Task InitializeAsync()
    {
        await _initializeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(basePath))
            {
                basePath = AppContext.BaseDirectory;
            }

            var dbDirectory = Path.Combine(basePath, "SystemGuardian");
            Directory.CreateDirectory(dbDirectory);

            var dbPath = Path.Combine(dbDirectory, "systemguardian.db");
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            _connectionString = builder.ToString();

            await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
            await EnsureSchemaAsync(connection).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }

        await FlushPendingAsync(250).ConfigureAwait(false);
    }

    public async Task<WriteConfirm> LogAsync(string tickId, string logType, object payload)
    {
        var normalizedType = NormalizeLogType(logType);
        var confirm = new WriteConfirm
        {
            TickId = tickId ?? string.Empty,
            LogType = normalizedType,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            if (!_initialized)
            {
                await InitializeAsync().ConfigureAwait(false);
            }

            var stopwatch = Stopwatch.StartNew();
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
                var recordId = await WriteLogAsync(connection, confirm.TickId, normalizedType, payload, DateTime.UtcNow)
                    .ConfigureAwait(false);
                confirm.RecordId = ToIntRecordId(recordId);
                confirm.Success = true;
            }
            finally
            {
                _writeLock.Release();
            }

            if (stopwatch.ElapsedMilliseconds < 150)
            {
                _ = FlushPendingBestEffortAsync();
            }
        }
        catch (Exception ex)
        {
            EnqueuePending(new PendingLog(confirm.TickId, normalizedType, payload, DateTime.UtcNow));
            confirm.Success = false;
            confirm.Error = $"Queued because SQLite write failed: {ex.Message}";
        }

        return confirm;
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;").ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;").ConfigureAwait(false);

        var schema = """
CREATE TABLE IF NOT EXISTS audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tick_id TEXT NOT NULL,
    log_type TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS kill_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tick_id TEXT NOT NULL,
    log_type TEXT NOT NULL,
    process_name TEXT NOT NULL,
    pid INTEGER NOT NULL,
    action_taken TEXT NOT NULL,
    action_result TEXT NOT NULL,
    reason_text TEXT NOT NULL,
    cpu_at_action REAL,
    ram_at_action REAL,
    duration_ms REAL,
    user_feedback INTEGER DEFAULT NULL,
    payload_json TEXT NOT NULL,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS agent_error_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tick_id TEXT NOT NULL,
    agent_name TEXT NOT NULL,
    error_type TEXT NOT NULL,
    error_message TEXT NOT NULL,
    error_stack TEXT,
    payload_json TEXT NOT NULL,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tier_change_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tick_id TEXT NOT NULL,
    old_tier INTEGER,
    new_tier INTEGER,
    worst_resource TEXT,
    current_pct REAL,
    payload_json TEXT NOT NULL,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS system_recovery_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tick_id TEXT NOT NULL,
    recovered_resource TEXT NOT NULL,
    previous_pct REAL,
    current_pct REAL,
    payload_json TEXT NOT NULL,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS usage_snapshots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tick_id TEXT NOT NULL,
    cpu_pct REAL,
    ram_pct REAL,
    gpu_pct REAL,
    disk_pct REAL,
    network_bps REAL,
    thermal_cpu_c REAL,
    anomalies_json TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS process_trust_scores (
    process_name TEXT PRIMARY KEY,
    trust_score REAL DEFAULT 0.5,
    kill_count INTEGER DEFAULT 0,
    suspend_count INTEGER DEFAULT 0,
    last_seen TEXT,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_audit_log_timestamp ON audit_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_audit_log_tick ON audit_log(tick_id);
CREATE INDEX IF NOT EXISTS idx_kill_log_timestamp ON kill_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_kill_log_process ON kill_log(process_name);
CREATE INDEX IF NOT EXISTS idx_agent_error_timestamp ON agent_error_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_tier_change_timestamp ON tier_change_log(timestamp);
CREATE INDEX IF NOT EXISTS idx_usage_snapshots_timestamp ON usage_snapshots(timestamp);
""";

        await ExecuteNonQueryAsync(connection, schema).ConfigureAwait(false);
    }

    private async Task<long> WriteLogAsync(
        SqliteConnection connection,
        string tickId,
        string logType,
        object? payload,
        DateTime timestamp)
    {
        var payloadJson = SerializePayload(payload);
        var recordId = logType switch
        {
            "ACTION_TAKEN" => await InsertKillLogAsync(connection, tickId, logType, payload, payloadJson, timestamp).ConfigureAwait(false),
            "WHITELIST_BLOCKED" => await InsertKillLogAsync(connection, tickId, logType, payload, payloadJson, timestamp).ConfigureAwait(false),
            "TIER_CHANGE" => await InsertTierChangeAsync(connection, tickId, payload, payloadJson, timestamp).ConfigureAwait(false),
            "AGENT_ERROR" => await InsertAgentErrorAsync(connection, tickId, logType, payload, payloadJson, timestamp).ConfigureAwait(false),
            "AGENT_TIMEOUT" => await InsertAgentErrorAsync(connection, tickId, logType, payload, payloadJson, timestamp).ConfigureAwait(false),
            "SYSTEM_RECOVERY" => await InsertSystemRecoveryAsync(connection, tickId, payload, payloadJson, timestamp).ConfigureAwait(false),
            "USAGE_SNAPSHOT" => await InsertUsageSnapshotAsync(connection, tickId, payload, payloadJson, timestamp).ConfigureAwait(false),
            "METRIC_SNAPSHOT" => await InsertUsageSnapshotAsync(connection, tickId, payload, payloadJson, timestamp).ConfigureAwait(false),
            _ => await InsertAuditAsync(connection, tickId, logType, payloadJson, timestamp).ConfigureAwait(false)
        };

        if (logType != "AUDIT_LOG")
        {
            await InsertAuditAsync(connection, tickId, logType, payloadJson, timestamp).ConfigureAwait(false);
        }

        return recordId;
    }

    private async Task<long> InsertKillLogAsync(
        SqliteConnection connection,
        string tickId,
        string logType,
        object? payload,
        string payloadJson,
        DateTime timestamp)
    {
        var processName = "Unknown";
        var pid = 0;
        var action = logType == "WHITELIST_BLOCKED" ? "BLOCKED" : "UNKNOWN";
        var result = logType == "WHITELIST_BLOCKED" ? "BLOCKED" : "UNKNOWN";
        var reason = string.Empty;
        double? durationMs = null;

        if (payload is ExecutionResult execution)
        {
            processName = execution.TargetName;
            pid = execution.TargetPid;
            action = execution.ActionAttempted;
            result = execution.ActionResult;
            reason = execution.PlainEnglish;
            durationMs = execution.DurationMs;
        }
        else if (payload is GuardDecision guard)
        {
            processName = guard.TargetName;
            pid = guard.TargetPid;
            action = "BLOCKED";
            result = guard.Decision;
            reason = guard.BlockReason;
        }
        else if (payload is OrchestratorDecision.ActionToExecute actionToExecute)
        {
            processName = actionToExecute.TargetName;
            pid = actionToExecute.TargetPid;
            action = actionToExecute.ApprovedAction;
            result = "DISPATCHED";
            reason = actionToExecute.ApprovedBy;
        }
        else
        {
            processName = GetString(payload, "target_name", "TargetName", "name", "process") ?? processName;
            pid = GetInt(payload, "target_pid", "TargetPid", "pid") ?? pid;
            action = GetString(payload, "action", "Action", "action_taken", "ActionAttempted", "approvedAction") ?? action;
            result = GetString(payload, "result", "Result", "action_result", "ActionResult", "decision") ?? result;
            reason = GetString(payload, "plain_english", "PlainEnglish", "block_reason", "BlockReason", "reason", "error") ?? reason;
            durationMs = GetDouble(payload, "duration_ms", "DurationMs");
        }

        var cpuAtAction = GetDouble(payload, "cpu_at_action", "CpuAtAction", "cpuPct", "cpu");
        var ramAtAction = GetDouble(payload, "ram_at_action", "RamAtAction", "ramPct", "ram");

        var recordId = await InsertAndReturnIdAsync(
            connection,
            """
INSERT INTO kill_log
    (tick_id, log_type, process_name, pid, action_taken, action_result, reason_text, cpu_at_action, ram_at_action, duration_ms, payload_json, timestamp)
VALUES
    ($tick_id, $log_type, $process_name, $pid, $action_taken, $action_result, $reason_text, $cpu_at_action, $ram_at_action, $duration_ms, $payload_json, $timestamp);
SELECT last_insert_rowid();
""",
            ("$tick_id", tickId),
            ("$log_type", logType),
            ("$process_name", string.IsNullOrWhiteSpace(processName) ? "Unknown" : processName),
            ("$pid", pid),
            ("$action_taken", action),
            ("$action_result", result),
            ("$reason_text", reason),
            ("$cpu_at_action", ToDbValue(cpuAtAction)),
            ("$ram_at_action", ToDbValue(ramAtAction)),
            ("$duration_ms", ToDbValue(durationMs)),
            ("$payload_json", payloadJson),
            ("$timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);

        await UpdateTrustScoreAsync(connection, processName, action, result, timestamp).ConfigureAwait(false);
        return recordId;
    }

    private async Task<long> InsertAgentErrorAsync(
        SqliteConnection connection,
        string tickId,
        string logType,
        object? payload,
        string payloadJson,
        DateTime timestamp)
    {
        var agentName = GetString(payload, "agent", "agent_id", "agentName", "AgentName") ?? "Unknown";
        var errorMessage = GetString(payload, "error", "error_message", "ErrorMessage", "message") ??
                           (logType == "AGENT_TIMEOUT" ? "Agent timed out." : "Agent error.");
        var stack = GetString(payload, "stack", "error_stack", "StackTrace");

        return await InsertAndReturnIdAsync(
            connection,
            """
INSERT INTO agent_error_log
    (tick_id, agent_name, error_type, error_message, error_stack, payload_json, timestamp)
VALUES
    ($tick_id, $agent_name, $error_type, $error_message, $error_stack, $payload_json, $timestamp);
SELECT last_insert_rowid();
""",
            ("$tick_id", tickId),
            ("$agent_name", agentName),
            ("$error_type", logType),
            ("$error_message", errorMessage),
            ("$error_stack", ToDbValue(stack)),
            ("$payload_json", payloadJson),
            ("$timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    private async Task<long> InsertTierChangeAsync(
        SqliteConnection connection,
        string tickId,
        object? payload,
        string payloadJson,
        DateTime timestamp)
    {
        return await InsertAndReturnIdAsync(
            connection,
            """
INSERT INTO tier_change_log
    (tick_id, old_tier, new_tier, worst_resource, current_pct, payload_json, timestamp)
VALUES
    ($tick_id, $old_tier, $new_tier, $worst_resource, $current_pct, $payload_json, $timestamp);
SELECT last_insert_rowid();
""",
            ("$tick_id", tickId),
            ("$old_tier", ToDbValue(GetInt(payload, "from", "old_tier", "PreviousTier"))),
            ("$new_tier", ToDbValue(GetInt(payload, "to", "new_tier", "CurrentTier"))),
            ("$worst_resource", ToDbValue(GetString(payload, "worst_resource", "WorstResource"))),
            ("$current_pct", ToDbValue(GetDouble(payload, "current_pct", "WorstResourcePct"))),
            ("$payload_json", payloadJson),
            ("$timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    private async Task<long> InsertSystemRecoveryAsync(
        SqliteConnection connection,
        string tickId,
        object? payload,
        string payloadJson,
        DateTime timestamp)
    {
        return await InsertAndReturnIdAsync(
            connection,
            """
INSERT INTO system_recovery_log
    (tick_id, recovered_resource, previous_pct, current_pct, payload_json, timestamp)
VALUES
    ($tick_id, $recovered_resource, $previous_pct, $current_pct, $payload_json, $timestamp);
SELECT last_insert_rowid();
""",
            ("$tick_id", tickId),
            ("$recovered_resource", GetString(payload, "recovered_resource", "RecoveredResource", "resource") ?? "Unknown"),
            ("$previous_pct", ToDbValue(GetDouble(payload, "previous_pct", "PreviousPct"))),
            ("$current_pct", ToDbValue(GetDouble(payload, "current_pct", "CurrentPct"))),
            ("$payload_json", payloadJson),
            ("$timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    private async Task<long> InsertUsageSnapshotAsync(
        SqliteConnection connection,
        string tickId,
        object? payload,
        string payloadJson,
        DateTime timestamp)
    {
        double? cpuPct = null;
        double? ramPct = null;
        double? gpuPct = null;
        double? diskPct = null;
        double? networkBps = null;
        double? thermalCpuC = null;
        var anomaliesJson = "[]";

        if (payload is MetricSnapshot metrics)
        {
            cpuPct = metrics.Cpu.OverallPct;
            ramPct = metrics.Ram.UsagePct;
            gpuPct = metrics.Gpu.UtilisationPct;
            diskPct = metrics.Disk.Drives.Count == 0 ? null : metrics.Disk.Drives.Max(d => d.UsagePct);
            networkBps = Math.Max(0, metrics.Network.SentBps) + Math.Max(0, metrics.Network.RecvBps);
            thermalCpuC = metrics.Thermal.CpuTempCelsius;
            anomaliesJson = SerializePayload(metrics.Anomalies);
        }
        else
        {
            cpuPct = GetDouble(payload, "cpu_pct", "CpuPct", "cpu");
            ramPct = GetDouble(payload, "ram_pct", "RamPct", "ram");
            gpuPct = GetDouble(payload, "gpu_pct", "GpuPct", "gpu");
            diskPct = GetDouble(payload, "disk_pct", "DiskPct", "disk");
            networkBps = GetDouble(payload, "network_bps", "NetworkBps", "network");
            thermalCpuC = GetDouble(payload, "thermal_cpu_c", "ThermalCpuC");
            anomaliesJson = SerializePayload(GetProperty(payload, "anomalies", "Anomalies") ?? Array.Empty<string>());
        }

        return await InsertAndReturnIdAsync(
            connection,
            """
INSERT INTO usage_snapshots
    (tick_id, cpu_pct, ram_pct, gpu_pct, disk_pct, network_bps, thermal_cpu_c, anomalies_json, payload_json, timestamp)
VALUES
    ($tick_id, $cpu_pct, $ram_pct, $gpu_pct, $disk_pct, $network_bps, $thermal_cpu_c, $anomalies_json, $payload_json, $timestamp);
SELECT last_insert_rowid();
""",
            ("$tick_id", tickId),
            ("$cpu_pct", ToDbValue(cpuPct)),
            ("$ram_pct", ToDbValue(ramPct)),
            ("$gpu_pct", ToDbValue(gpuPct)),
            ("$disk_pct", ToDbValue(diskPct)),
            ("$network_bps", ToDbValue(networkBps)),
            ("$thermal_cpu_c", ToDbValue(thermalCpuC)),
            ("$anomalies_json", anomaliesJson),
            ("$payload_json", payloadJson),
            ("$timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    private async Task<long> InsertAuditAsync(
        SqliteConnection connection,
        string tickId,
        string logType,
        string payloadJson,
        DateTime timestamp)
    {
        return await InsertAndReturnIdAsync(
            connection,
            """
INSERT INTO audit_log
    (tick_id, log_type, payload_json, timestamp)
VALUES
    ($tick_id, $log_type, $payload_json, $timestamp);
SELECT last_insert_rowid();
""",
            ("$tick_id", tickId),
            ("$log_type", logType),
            ("$payload_json", payloadJson),
            ("$timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    private async Task UpdateTrustScoreAsync(
        SqliteConnection connection,
        string processName,
        string action,
        string result,
        DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(processName) ||
            !string.Equals(result, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isKill = string.Equals(action, "FORCE_KILL", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(action, "GRACEFUL_CLOSE", StringComparison.OrdinalIgnoreCase);
        var isSuspend = string.Equals(action, "SUSPEND", StringComparison.OrdinalIgnoreCase);

        if (!isKill && !isSuspend)
        {
            return;
        }

        await ExecuteNonQueryAsync(
            connection,
            """
INSERT INTO process_trust_scores
    (process_name, trust_score, kill_count, suspend_count, last_seen, updated_at)
VALUES
    ($process_name, $trust_score, $kill_count, $suspend_count, $now, $now)
ON CONFLICT(process_name) DO UPDATE SET
    trust_score = max(0.0, min(1.0, process_trust_scores.trust_score + $delta)),
    kill_count = process_trust_scores.kill_count + $kill_count,
    suspend_count = process_trust_scores.suspend_count + $suspend_count,
    last_seen = $now,
    updated_at = $now;
""",
            ("$process_name", processName),
            ("$trust_score", isKill ? 0.45 : 0.5),
            ("$delta", isKill ? -0.05 : 0.0),
            ("$kill_count", isKill ? 1 : 0),
            ("$suspend_count", isSuspend ? 1 : 0),
            ("$now", timestamp.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    private async Task FlushPendingBestEffortAsync()
    {
        try
        {
            await FlushPendingAsync(100).ConfigureAwait(false);
        }
        catch
        {
            // The original LogAsync already reported the write outcome. Pending logs remain queued.
        }
    }

    private async Task FlushPendingAsync(int maxItems)
    {
        if (!_initialized || Volatile.Read(ref _pendingCount) == 0)
        {
            return;
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
            var flushed = 0;

            while (flushed < maxItems && _pendingLogs.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _pendingCount);

                try
                {
                    await WriteLogAsync(connection, pending.TickId, pending.LogType, pending.Payload, pending.QueuedAt)
                        .ConfigureAwait(false);
                    flushed++;
                }
                catch
                {
                    EnqueuePending(pending);
                    break;
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnqueuePending(PendingLog pending)
    {
        _pendingLogs.Enqueue(pending);
        var count = Interlocked.Increment(ref _pendingCount);

        while (count > MaxQueueSize && _pendingLogs.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _pendingCount);
        }
    }

    private string SerializePayload(object? payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload, _jsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                serialization_error = ex.Message,
                payload_type = payload?.GetType().FullName ?? "null"
            }, _jsonOptions);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<long> InsertAndReturnIdAsync(
        SqliteConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        AddParameters(command, parameters);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static object ToDbValue(object? value) => value ?? DBNull.Value;

    private static int ToIntRecordId(long recordId) =>
        recordId > int.MaxValue ? int.MaxValue : (int)recordId;

    private static string NormalizeLogType(string? logType)
    {
        if (string.IsNullOrWhiteSpace(logType))
        {
            return "AUDIT_LOG";
        }

        return logType.Trim().ToUpperInvariant() switch
        {
            "METRICS" => "METRIC_SNAPSHOT",
            "USAGE" => "USAGE_SNAPSHOT",
            "ERROR" => "AGENT_ERROR",
            "TIMEOUT" => "AGENT_TIMEOUT",
            var normalized => normalized
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

    private static double? GetDouble(object? source, params string[] names)
    {
        var value = GetProperty(source, names);
        return value switch
        {
            null => null,
            double number => number,
            float number => number,
            decimal number => (double)number,
            int number => number,
            long number => number,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ when double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
