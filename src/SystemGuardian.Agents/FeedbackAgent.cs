using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.ML;
using Microsoft.ML.Data;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-10: Feedback Agent - stores user labels and builds retraining artifacts.
/// It never changes a live action decision; model replacement is gated by validation accuracy.
/// </summary>
public class FeedbackAgent : IFeedbackAgent
{
    private const int MinRetrainSamples = 20;
    private const float RequiredAccuracyImprovement = 0.02f;

    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly SemaphoreSlim _retrainLock = new(1, 1);
    private readonly MLContext _mlContext = new(seed: 10);
    private string _connectionString = string.Empty;
    private string _modelDirectory = string.Empty;
    private bool _initialized;

    public int AgentId => 10;
    public string AgentName => "Feedback Agent";

    public async Task InitializeAsync()
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

        var appDirectory = Path.Combine(basePath, "SystemGuardian");
        Directory.CreateDirectory(appDirectory);
        _modelDirectory = Path.Combine(appDirectory, "Models");
        Directory.CreateDirectory(_modelDirectory);

        var dbPath = Path.Combine(appDirectory, "systemguardian.db");
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

    public async Task<FeedbackConfirm> RecordFeedbackAsync(int actionId, bool wasCorrect, string? userNote = null)
    {
        var confirm = new FeedbackConfirm
        {
            ActionId = actionId,
            WasCorrect = wasCorrect
        };

        try
        {
            if (actionId <= 0)
            {
                confirm.Error = "ActionId must refer to a positive kill_log record id.";
                return confirm;
            }

            await InitializeAsync().ConfigureAwait(false);
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

                var action = await GetActionRecordAsync(connection, actionId).ConfigureAwait(false);
                if (action == null)
                {
                    confirm.Error = $"No logged action exists for id {actionId}.";
                    return confirm;
                }

                var feedbackValue = wasCorrect ? 1 : 0;
                var feedbackText = wasCorrect ? "GOOD" : "BAD";
                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var note = SanitizeUserNote(userNote);

                await ExecuteNonQueryAsync(
                    connection,
                    """
UPDATE kill_log
SET user_feedback = $feedback
WHERE id = $id;
""",
                    ("$feedback", feedbackValue),
                    ("$id", actionId)).ConfigureAwait(false);

                await ExecuteNonQueryAsync(
                    connection,
                    """
INSERT INTO user_feedback
    (action_id, timestamp, process_name, process_id, action_type, feedback, user_comment, processed, payload_json)
VALUES
    ($action_id, $timestamp, $process_name, $process_id, $action_type, $feedback, $user_comment, 0, $payload_json);
""",
                    ("$action_id", actionId),
                    ("$timestamp", now),
                    ("$process_name", action.ProcessName),
                    ("$process_id", action.Pid),
                    ("$action_type", action.ActionTaken),
                    ("$feedback", feedbackText),
                    ("$user_comment", ToDbValue(note)),
                    ("$payload_json", JsonSerializer.Serialize(new
                    {
                        action_id = actionId,
                        was_correct = wasCorrect,
                        user_note = note
                    }))).ConfigureAwait(false);

                await UpdateTrustFromFeedbackAsync(connection, action.ProcessName, wasCorrect, now).ConfigureAwait(false);
                await InsertTrainingDataAsync(connection, action, feedbackText, now).ConfigureAwait(false);
                await InsertAuditAsync(connection, "FEEDBACK_RECORDED", new
                {
                    action_id = actionId,
                    process_name = action.ProcessName,
                    action = action.ActionTaken,
                    feedback = feedbackText
                }, now).ConfigureAwait(false);

                await transaction.CommitAsync().ConfigureAwait(false);
                confirm.Stored = true;
            }
            finally
            {
                _dbLock.Release();
            }
        }
        catch (Exception ex)
        {
            confirm.Stored = false;
            confirm.Error = ex.Message;
        }

        return confirm;
    }

    public async Task<ModelUpdateReport> RetrainModelAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        var report = new ModelUpdateReport();

        if (!await _retrainLock.WaitAsync(0).ConfigureAwait(false))
        {
            report.Decision = "SKIPPED";
            report.SkipReason = "Retraining already running.";
            return report;
        }

        try
        {
            var activeTier = GetCurrentTierHint();
            if (activeTier >= 3)
            {
                report.Decision = "SKIPPED";
                report.SkipReason = $"Retraining skipped because active tier is {activeTier}.";
                await LogRetrainReportAsync(report).ConfigureAwait(false);
                return report;
            }

            await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
            await EnsureSchemaAsync(connection).ConfigureAwait(false);

            var state = await LoadRetrainStateAsync(connection).ConfigureAwait(false);
            var samples = await LoadTrainingSamplesAsync(connection, state.LastFeedbackId).ConfigureAwait(false);
            report.SamplesUsed = samples.Count;
            report.OldModelAccuracy = state.CurrentAccuracy;

            if (samples.Count < MinRetrainSamples)
            {
                report.Decision = "SKIPPED";
                report.SkipReason = $"Only {samples.Count} new labelled record(s); need {MinRetrainSamples}.";
                await LogRetrainReportAsync(report).ConfigureAwait(false);
                return report;
            }

            if (samples.Select(s => s.Label).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                report.Decision = "SKIPPED";
                report.SkipReason = "Retraining requires at least two action labels.";
                await LogRetrainReportAsync(report).ConfigureAwait(false);
                return report;
            }

            var data = _mlContext.Data.LoadFromEnumerable(samples);
            var split = _mlContext.Data.TrainTestSplit(data, testFraction: 0.2, seed: 10);

            var pipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label")
                .Append(_mlContext.Transforms.Concatenate("Features",
                    nameof(TrainingSample.F1CpuPct),
                    nameof(TrainingSample.F2RamPct),
                    nameof(TrainingSample.F3DurationMs),
                    nameof(TrainingSample.F4ActionRisk),
                    nameof(TrainingSample.F5WasKill),
                    nameof(TrainingSample.F6WasSuspend),
                    nameof(TrainingSample.F7WasThrottle),
                    nameof(TrainingSample.F8TrustScore),
                    nameof(TrainingSample.F9KillCount),
                    nameof(TrainingSample.F10SuspendCount)))
                .Append(_mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy())
                .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            var model = pipeline.Fit(split.TrainSet);
            var predictions = model.Transform(split.TestSet);
            var metrics = _mlContext.MulticlassClassification.Evaluate(predictions);
            report.NewModelAccuracy = (float)metrics.MacroAccuracy;
            report.ConfusionMatrix = BuildConfusionMatrix(predictions);

            if (report.NewModelAccuracy >= report.OldModelAccuracy + RequiredAccuracyImprovement)
            {
                var modelPath = Path.Combine(_modelDirectory, "action-ranker-feedback.zip");
                _mlContext.Model.Save(model, split.TrainSet.Schema, modelPath);
                report.Decision = "MODEL_UPDATED";
                report.SkipReason = string.Empty;
                await SaveRetrainStateAsync(connection, report.NewModelAccuracy, samples.Max(s => s.FeedbackId)).ConfigureAwait(false);
                await MarkFeedbackProcessedAsync(connection, samples.Max(s => s.FeedbackId)).ConfigureAwait(false);
            }
            else
            {
                report.Decision = "MODEL_REJECTED";
                report.SkipReason = $"Accuracy {report.NewModelAccuracy:P1} did not improve on {report.OldModelAccuracy:P1} by {RequiredAccuracyImprovement:P0}.";
            }

            await LogRetrainReportAsync(report).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.Decision = "SKIPPED";
            report.SkipReason = $"Retraining failed: {ex.Message}";
            await LogRetrainReportAsync(report).ConfigureAwait(false);
        }
        finally
        {
            _retrainLock.Release();
        }

        return report;
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

        var schema = """
CREATE TABLE IF NOT EXISTS user_feedback (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    action_id INTEGER NOT NULL,
    timestamp TEXT NOT NULL,
    process_name TEXT NOT NULL,
    process_id INTEGER NOT NULL,
    action_type TEXT NOT NULL,
    feedback TEXT NOT NULL,
    user_comment TEXT,
    processed INTEGER NOT NULL DEFAULT 0,
    processed_timestamp TEXT,
    payload_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ml_training_data (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    action_id INTEGER NOT NULL,
    feedback_id INTEGER,
    process_features_json TEXT NOT NULL,
    correct_category TEXT NOT NULL,
    from_user_feedback INTEGER NOT NULL,
    processed INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS feedback_retrain_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    last_feedback_id INTEGER NOT NULL DEFAULT 0,
    current_accuracy REAL NOT NULL DEFAULT 0.50,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_user_feedback_action ON user_feedback(action_id);
CREATE INDEX IF NOT EXISTS idx_user_feedback_processed ON user_feedback(processed);
CREATE INDEX IF NOT EXISTS idx_training_data_action ON ml_training_data(action_id);
""";

        await ExecuteNonQueryAsync(connection, schema).ConfigureAwait(false);
    }

    private static async Task<ActionRecord?> GetActionRecordAsync(SqliteConnection connection, int actionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT k.id, k.process_name, k.pid, k.action_taken, k.cpu_at_action, k.ram_at_action, k.duration_ms,
       COALESCE(t.trust_score, 0.5), COALESCE(t.kill_count, 0), COALESCE(t.suspend_count, 0)
FROM kill_log k
LEFT JOIN process_trust_scores t ON lower(t.process_name) = lower(k.process_name)
WHERE k.id = $id
LIMIT 1;
""";
        command.Parameters.AddWithValue("$id", actionId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        return new ActionRecord(
            Id: reader.GetInt32(0),
            ProcessName: reader.GetString(1),
            Pid: reader.GetInt32(2),
            ActionTaken: reader.GetString(3),
            CpuAtAction: GetNullableFloat(reader, 4),
            RamAtAction: GetNullableFloat(reader, 5),
            DurationMs: GetNullableFloat(reader, 6),
            TrustScore: GetNullableFloat(reader, 7) ?? 0.5f,
            KillCount: GetNullableFloat(reader, 8) ?? 0f,
            SuspendCount: GetNullableFloat(reader, 9) ?? 0f);
    }

    private static async Task UpdateTrustFromFeedbackAsync(
        SqliteConnection connection,
        string processName,
        bool wasCorrect,
        string timestamp)
    {
        var delta = wasCorrect ? 0.10 : -0.15;
        await ExecuteNonQueryAsync(
            connection,
            """
INSERT INTO process_trust_scores
    (process_name, trust_score, kill_count, suspend_count, last_seen, updated_at)
VALUES
    ($process_name, max(0.0, min(1.0, 0.5 + $delta)), 0, 0, $now, $now)
ON CONFLICT(process_name) DO UPDATE SET
    trust_score = max(0.0, min(1.0, process_trust_scores.trust_score + $delta)),
    last_seen = $now,
    updated_at = $now;
""",
            ("$process_name", processName),
            ("$delta", delta),
            ("$now", timestamp)).ConfigureAwait(false);
    }

    private static async Task InsertTrainingDataAsync(
        SqliteConnection connection,
        ActionRecord action,
        string feedback,
        string timestamp)
    {
        var label = DeriveCorrectCategory(action.ActionTaken, feedback);
        var features = BuildFeaturePayload(action, feedback);

        await ExecuteNonQueryAsync(
            connection,
            """
INSERT INTO ml_training_data
    (timestamp, action_id, process_features_json, correct_category, from_user_feedback, processed)
VALUES
    ($timestamp, $action_id, $features, $category, 1, 0);
""",
            ("$timestamp", timestamp),
            ("$action_id", action.Id),
            ("$features", JsonSerializer.Serialize(features)),
            ("$category", label)).ConfigureAwait(false);
    }

    private static async Task<RetrainState> LoadRetrainStateAsync(SqliteConnection connection)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
INSERT OR IGNORE INTO feedback_retrain_state (id, last_feedback_id, current_accuracy, updated_at)
VALUES (1, 0, 0.50, $now);
""",
            ("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_feedback_id, current_accuracy FROM feedback_retrain_state WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return new RetrainState(reader.GetInt32(0), (float)reader.GetDouble(1));
        }

        return new RetrainState(0, 0.5f);
    }

    private static async Task SaveRetrainStateAsync(SqliteConnection connection, float accuracy, int lastFeedbackId)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
UPDATE feedback_retrain_state
SET last_feedback_id = $last_feedback_id,
    current_accuracy = $accuracy,
    updated_at = $now
WHERE id = 1;
""",
            ("$last_feedback_id", lastFeedbackId),
            ("$accuracy", accuracy),
            ("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    private static async Task<List<TrainingSample>> LoadTrainingSamplesAsync(SqliteConnection connection, int lastFeedbackId)
    {
        var samples = new List<TrainingSample>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT f.id, k.id, k.action_taken, k.cpu_at_action, k.ram_at_action, k.duration_ms,
       f.feedback, COALESCE(t.trust_score, 0.5), COALESCE(t.kill_count, 0), COALESCE(t.suspend_count, 0)
FROM user_feedback f
JOIN kill_log k ON k.id = f.action_id
LEFT JOIN process_trust_scores t ON lower(t.process_name) = lower(k.process_name)
WHERE f.id > $last_feedback_id
  AND f.feedback IN ('GOOD', 'BAD')
ORDER BY f.id ASC;
""";
        command.Parameters.AddWithValue("$last_feedback_id", lastFeedbackId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var feedbackId = reader.GetInt32(0);
            var actionId = reader.GetInt32(1);
            var action = reader.GetString(2);
            var feedback = reader.GetString(6);
            var cpu = GetNullableFloat(reader, 3) ?? 0f;
            var ram = GetNullableFloat(reader, 4) ?? 0f;
            var duration = GetNullableFloat(reader, 5) ?? 0f;
            var trust = GetNullableFloat(reader, 7) ?? 0.5f;
            var killCount = GetNullableFloat(reader, 8) ?? 0f;
            var suspendCount = GetNullableFloat(reader, 9) ?? 0f;

            samples.Add(new TrainingSample
            {
                FeedbackId = feedbackId,
                ActionId = actionId,
                Label = DeriveCorrectCategory(action, feedback),
                F1CpuPct = Math.Clamp(cpu, 0f, 100f),
                F2RamPct = Math.Clamp(ram, 0f, 100f),
                F3DurationMs = Math.Clamp(duration / 10_000f, 0f, 1f),
                F4ActionRisk = ActionRisk(action),
                F5WasKill = IsKillAction(action) ? 1f : 0f,
                F6WasSuspend = string.Equals(action, "SUSPEND", StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
                F7WasThrottle = string.Equals(action, "THROTTLE", StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
                F8TrustScore = Math.Clamp(trust, 0f, 1f),
                F9KillCount = Math.Clamp(killCount / 10f, 0f, 1f),
                F10SuspendCount = Math.Clamp(suspendCount / 10f, 0f, 1f)
            });
        }

        return samples;
    }

    private static async Task MarkFeedbackProcessedAsync(SqliteConnection connection, int maxFeedbackId)
    {
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await ExecuteNonQueryAsync(
            connection,
            """
UPDATE user_feedback
SET processed = 1,
    processed_timestamp = $now
WHERE id <= $max_id;

UPDATE ml_training_data
SET processed = 1
WHERE action_id IN (SELECT action_id FROM user_feedback WHERE id <= $max_id);
""",
            ("$now", now),
            ("$max_id", maxFeedbackId)).ConfigureAwait(false);
    }

    private async Task LogRetrainReportAsync(ModelUpdateReport report)
    {
        try
        {
            await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
            await InsertAuditAsync(connection, "MODEL_RETRAIN", report, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
        catch
        {
            // Retrain reports are returned to the caller even if secondary audit logging fails.
        }
    }

    private static async Task InsertAuditAsync(SqliteConnection connection, string logType, object payload, string timestamp)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
INSERT INTO audit_log
    (tick_id, log_type, payload_json, timestamp)
VALUES
    ($tick_id, $log_type, $payload_json, $timestamp);
""",
            ("$tick_id", "feedback"),
            ("$log_type", logType),
            ("$payload_json", JsonSerializer.Serialize(payload)),
            ("$timestamp", timestamp)).ConfigureAwait(false);
    }

    private static Dictionary<string, Dictionary<string, int>> BuildConfusionMatrix(IDataView predictions)
    {
        var rows = _emptyMatrix();
        var enumerable = new MLContext(seed: 10).Data.CreateEnumerable<PredictionRow>(predictions, reuseRowObject: false);
        foreach (var row in enumerable)
        {
            var actual = string.IsNullOrWhiteSpace(row.Label) ? "UNKNOWN" : row.Label;
            var predicted = string.IsNullOrWhiteSpace(row.PredictedLabel) ? "UNKNOWN" : row.PredictedLabel;
            if (!rows.TryGetValue(actual, out var inner))
            {
                inner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                rows[actual] = inner;
            }

            inner[predicted] = inner.TryGetValue(predicted, out var count) ? count + 1 : 1;
        }

        return rows;

        static Dictionary<string, Dictionary<string, int>> _emptyMatrix() => new(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object> BuildFeaturePayload(ActionRecord action, string feedback)
    {
        return new Dictionary<string, object>
        {
            ["F1_cpu_pct"] = Math.Clamp(action.CpuAtAction ?? 0f, 0f, 100f),
            ["F2_ram_pct"] = Math.Clamp(action.RamAtAction ?? 0f, 0f, 100f),
            ["F3_duration_ms"] = action.DurationMs ?? 0f,
            ["F4_action_risk"] = ActionRisk(action.ActionTaken),
            ["F5_was_kill"] = IsKillAction(action.ActionTaken) ? 1 : 0,
            ["F6_was_suspend"] = string.Equals(action.ActionTaken, "SUSPEND", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            ["F7_was_throttle"] = string.Equals(action.ActionTaken, "THROTTLE", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            ["F8_trust_score"] = action.TrustScore,
            ["F9_kill_count"] = action.KillCount,
            ["F10_suspend_count"] = action.SuspendCount,
            ["feedback"] = feedback
        };
    }

    private static string DeriveCorrectCategory(string action, string feedback)
    {
        if (string.Equals(feedback, "GOOD", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeActionLabel(action);
        }

        return action.ToUpperInvariant() switch
        {
            "FORCE_KILL" => "GRACEFUL_CLOSE",
            "GRACEFUL_CLOSE" => "SUSPEND",
            "SUSPEND" => "THROTTLE",
            "THROTTLE" => "LEAVE_UNTOUCHED",
            _ => "LEAVE_UNTOUCHED"
        };
    }

    private static string NormalizeActionLabel(string action) =>
        action.ToUpperInvariant() switch
        {
            "FORCE_KILL" => "FORCE_KILL",
            "GRACEFUL_CLOSE" => "GRACEFUL_CLOSE",
            "SUSPEND" => "SUSPEND",
            "THROTTLE" => "THROTTLE",
            _ => "LEAVE_UNTOUCHED"
        };

    private static float ActionRisk(string action) =>
        action.ToUpperInvariant() switch
        {
            "FORCE_KILL" => 1.0f,
            "GRACEFUL_CLOSE" => 0.75f,
            "SUSPEND" => 0.5f,
            "THROTTLE" => 0.25f,
            _ => 0f
        };

    private static bool IsKillAction(string action) =>
        string.Equals(action, "FORCE_KILL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(action, "GRACEFUL_CLOSE", StringComparison.OrdinalIgnoreCase);

    private static int GetCurrentTierHint()
    {
        var raw = Environment.GetEnvironmentVariable("SYSTEMGUARDIAN_CURRENT_TIER");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier)
            ? tier
            : 1;
    }

    private static string? SanitizeUserNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        return note.Trim().Length <= 200 ? note.Trim() : note.Trim()[..200];
    }

    private static float? GetNullableFloat(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : Convert.ToSingle(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static object ToDbValue(object? value) => value ?? DBNull.Value;

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed record ActionRecord(
        int Id,
        string ProcessName,
        int Pid,
        string ActionTaken,
        float? CpuAtAction,
        float? RamAtAction,
        float? DurationMs,
        float TrustScore,
        float KillCount,
        float SuspendCount);

    private sealed record RetrainState(int LastFeedbackId, float CurrentAccuracy);

    private sealed class TrainingSample
    {
        [NoColumn] public int FeedbackId { get; set; }
        [NoColumn] public int ActionId { get; set; }
        public string Label { get; set; } = string.Empty;
        public float F1CpuPct { get; set; }
        public float F2RamPct { get; set; }
        public float F3DurationMs { get; set; }
        public float F4ActionRisk { get; set; }
        public float F5WasKill { get; set; }
        public float F6WasSuspend { get; set; }
        public float F7WasThrottle { get; set; }
        public float F8TrustScore { get; set; }
        public float F9KillCount { get; set; }
        public float F10SuspendCount { get; set; }
    }

    private sealed class PredictionRow
    {
        public string Label { get; set; } = string.Empty;
        public string PredictedLabel { get; set; } = string.Empty;
    }
}
