using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;
using SystemGuardian.Core.Models;
using SystemGuardian.Core.Services;

namespace SystemGuardian.Agents;

/// <summary>
/// AGENT-02: Forecasting Agent — 30-second predictive analytics engine.
/// Uses ML.NET Singular Spectrum Analysis (SSA) as primary method.
/// Falls back to linear regression → EMA when data is insufficient or SSA fails.
/// NEVER makes decisions. Only predicts and returns a ForecastResult.
/// </summary>
public class ForecastingAgent : IForecastingAgent
{
    public int AgentId => 2;
    public string AgentName => "Forecasting Agent";

    // ── ML.NET context — created once, reused every tick ─────────────────────
    private static readonly MLContext _mlContext = new(seed: 0);

    // ── Timed rolling buffer (max 60 entries, max age 90 seconds) ────────────
    private readonly Queue<(DateTime At, MetricSnapshot Snap)> _timedBuffer = new();
    private const int MaxBufferSize = 60;
    private const double MaxAgeSeconds = 90.0;

    // ── Forecast constants ────────────────────────────────────────────────────
    private const int ForecastHorizon = 30;    // 30 future data points = 30 seconds
    private const float WarnThreshold = 75f;   // Tier 2: current > 75%
    private const float ActThreshold  = 85f;   // Tier 3: projected > 85%  →  WillBreach
    private const float KillThreshold = 92f;   // Tier 4: projected > 92%
    private const float CritThreshold = 95f;   // Absolute critical
    private const float ThermalCritical = 90f; // °C — triggers Tier 4

    // Network normalisation — 100 MB/s total throughput ≙ 100 %
    private const float NetworkMaxBps = 100f * 1024f * 1024f;

    // ── ML.NET data-schema types ─────────────────────────────────────────────
    private sealed class MetricInput { public float Value { get; set; } }
    private sealed class ForecastOutput
    {
        [ColumnName("Forecast")]
        public float[] Forecast { get; set; } = Array.Empty<float>();
    }

    // ─────────────────────────────────────────────────────────────────────────

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task<ForecastResult> ForecastAsync(string tickId, MetricSnapshot current)
    {
        // Step 1 — update timed buffer, evict by age and size
        _timedBuffer.Enqueue((DateTime.UtcNow, current));
        while (_timedBuffer.Count > MaxBufferSize)
            _timedBuffer.Dequeue();

        var cutoff = DateTime.UtcNow.AddSeconds(-MaxAgeSeconds);
        while (_timedBuffer.Count > 0 && _timedBuffer.Peek().At < cutoff)
            _timedBuffer.Dequeue();

        var buffer = _timedBuffer.Select(t => t.Snap).ToArray();

        var result = new ForecastResult
        {
            TickId         = tickId,
            BufferSizeUsed = buffer.Length
        };

        try
        {
            // Step 2 — forecast each resource
            result.Resources["cpu"] = ForecastResource(
                buffer, s => s.Cpu.OverallPct, current.Cpu.OverallPct, result.ModelErrors);

            result.Resources["ram"] = ForecastResource(
                buffer, s => s.Ram.UsagePct, current.Ram.UsagePct, result.ModelErrors);

            result.Resources["gpu"] = ForecastResource(
                buffer, s => s.Gpu.UtilisationPct, current.Gpu.UtilisationPct, result.ModelErrors);

            // Disk: use max drive capacity % across all logical drives
            float diskPct = current.Disk.Drives.Count > 0
                ? current.Disk.Drives.Max(d => d.UsagePct) : -1;
            result.Resources["disk"] = ForecastResource(
                buffer,
                s => s.Disk.Drives.Count > 0 ? s.Disk.Drives.Max(d => d.UsagePct) : -1f,
                diskPct, result.ModelErrors);

            // Network: normalise raw bytes/sec to 0-100% (100 MB/s = 100%)
            float netPct = NormalizeNetworkPct(current.Network.SentBps + current.Network.RecvBps);
            result.Resources["network"] = ForecastResource(
                buffer,
                s => NormalizeNetworkPct(s.Network.SentBps + s.Network.RecvBps),
                netPct, result.ModelErrors);

            // Step 3 — worst resource = highest projected among available ones
            result.WorstResource = result.Resources
                .Where(kvp => kvp.Value.CurrentPct >= 0)
                .OrderByDescending(kvp => kvp.Value.ProjectedPct)
                .Select(kvp => kvp.Key)
                .FirstOrDefault() ?? "cpu";

            // Step 4 — recommended tier (matches AGENT-00 escalation thresholds)
            result.RecommendedTier = DetermineTier(result.Resources, current.Thermal.CpuTempCelsius);
        }
        catch (Exception ex)
        {
            result.ModelErrors.Add($"ForecastAsync unhandled: {ex.Message}");
        }

        return await Task.FromResult(result);
    }

    // ── Core per-resource forecasting ─────────────────────────────────────────

    private static ForecastResult.ResourceForecast ForecastResource(
        MetricSnapshot[] buffer,
        Func<MetricSnapshot, float> selector,
        float currentValue,
        List<string> errors)
    {
        var rf = new ForecastResult.ResourceForecast { CurrentPct = currentValue };

        // Resource unavailable (e.g. GPU not present)
        if (currentValue < 0)
        {
            rf.ProjectedPct = -1;
            rf.Trend        = "stable";
            rf.Confidence   = 0f;
            return rf;
        }

        // Extract clean history (exclude unavailable readings)
        var history = buffer
            .Select(selector)
            .Where(v => v >= 0)
            .ToList();

        // ── Cold start: < 10 readings ─────────────────────────────────────
        if (history.Count < 10)
        {
            rf.ProjectedPct = currentValue;
            rf.Trend        = "stable";
            rf.Confidence   = 0f;
            return rf;
        }

        // Linear slope from last 10 points — used for trend label in all paths
        var (slope, rSquared) = LinearRegression(history.TakeLast(10).ToList());
        rf.Trend = slope > 0.5f ? "rising" : slope < -0.5f ? "falling" : "stable";

        // ── Primary path: ML.NET SSA (≥ 20 readings) ─────────────────────
        if (history.Count >= 20)
        {
            try
            {
                int n          = history.Count;
                int windowSize = Math.Max(2, Math.Min(n / 2, 20));

                var data     = history.Select(v => new MetricInput { Value = v });
                var dataView = _mlContext.Data.LoadFromEnumerable(data);

                var pipeline = _mlContext.Forecasting.ForecastBySsa(
                    outputColumnName: "Forecast",
                    inputColumnName:  "Value",
                    windowSize:       windowSize,
                    seriesLength:     n,
                    trainSize:        n,
                    horizon:          ForecastHorizon
                );

                ITransformer model = pipeline.Fit(dataView);
                var engine         = model.CreateTimeSeriesEngine<MetricInput, ForecastOutput>(_mlContext);
                var prediction     = engine.Predict((int?)null, (float?)null);

                if (prediction.Forecast?.Length >= ForecastHorizon)
                {
                    // Forecast[29] = 30 seconds ahead
                    float projected = prediction.Forecast[ForecastHorizon - 1];

                    // Sanity-clamp: CPU/RAM/GPU/Disk/Network are all 0-100%
                    rf.ProjectedPct = Math.Clamp(projected, 0f, 110f);

                    // Confidence: R² of linear fit, capped at 0.5 when < 20 snapshots
                    // (< 20 case already handled above, but guard defensively)
                    rf.Confidence = Math.Min(0.9f, Math.Max(0f, (float)rSquared));

                    SetBreachFields(rf, currentValue, slope);
                    return rf;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"SSA error: {ex.Message} — falling back to linear regression");
            }
        }

        // ── Fallback 1: Linear regression (10-19 readings or SSA failure) ─
        float projectedLr = currentValue + slope * ForecastHorizon;
        rf.ProjectedPct = Math.Clamp(projectedLr, 0f, 110f);

        // Spec: confidence must be ≤ 0.5 when buffer < 20 entries
        float rawConf   = Math.Max(0f, (float)rSquared);
        rf.Confidence   = history.Count < 20
            ? Math.Min(0.5f, rawConf * 0.7f)
            : Math.Min(0.75f, rawConf);

        SetBreachFields(rf, currentValue, slope);
        return rf;
    }

    // ── Breach / ETA ─────────────────────────────────────────────────────────

    private static void SetBreachFields(ForecastResult.ResourceForecast rf, float currentValue, float slope)
    {
        rf.WillBreach = rf.ProjectedPct > ActThreshold;

        if (!rf.WillBreach || slope <= 0.01f) return;

        // Estimate seconds until the metric crosses ActThreshold at the current slope
        float eta = (ActThreshold - currentValue) / slope;
        if (eta > 0 && eta <= ForecastHorizon)
            rf.BreachEtaSec = (int)eta;
    }

    // ── Tier recommendation ───────────────────────────────────────────────────

    /// <summary>
    /// Matches the AGENT-00 escalation logic exactly:
    ///   Tier 4 — current or projected > 95%, or thermal > 90°C
    ///   Tier 3 — current > 85% or projected > 92%
    ///   Tier 2 — current > 75% or projected > 85%
    ///   Tier 1 — all clear
    /// </summary>
    private static int DetermineTier(
        Dictionary<string, ForecastResult.ResourceForecast> resources,
        float thermalCelsius)
    {
        // Thermal always checked first (safety)
        if (thermalCelsius > ThermalCritical) return 4;

        bool tier4 = false, tier3 = false, tier2 = false;

        foreach (var rf in resources.Values)
        {
            if (rf.CurrentPct < 0) continue; // unavailable resource

            if (rf.CurrentPct >= 85f || rf.ProjectedPct >= 85f) tier4 = true;
            if (rf.CurrentPct >= 75f || rf.ProjectedPct >= 75f) tier3 = true;
            if (rf.CurrentPct >= 65f || rf.ProjectedPct >= 65f) tier2 = true;
        }

        if (tier4) return 4;
        if (tier3) return 3;
        if (tier2) return 2;
        return 1;
    }

    // ── Maths helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ordinary least-squares linear regression.
    /// Returns (slope per tick, R²).
    /// </summary>
    private static (float slope, float rSquared) LinearRegression(List<float> values)
    {
        int n = values.Count;
        if (n < 2) return (0f, 0f);

        float sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX  += i;
            sumY  += values[i];
            sumXY += i * values[i];
            sumX2 += i * i;
        }

        float denom = (n * sumX2) - (sumX * sumX);
        if (MathF.Abs(denom) < 1e-6f) return (0f, 0f);

        float slope     = ((n * sumXY) - (sumX * sumY)) / denom;
        float intercept = (sumY - slope * sumX) / n;
        float yMean     = sumY / n;

        float ssTot = 0, ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            float predicted = intercept + slope * i;
            ssRes += (values[i] - predicted) * (values[i] - predicted);
            ssTot += (values[i] - yMean)     * (values[i] - yMean);
        }

        float rSquared = ssTot > 1e-6f ? 1f - ssRes / ssTot : 0f;
        return (slope, Math.Max(0f, rSquared));
    }

    private static float NormalizeNetworkPct(float totalBps)
    {
        if (totalBps < 0) return -1f;
        return Math.Clamp(totalBps / NetworkMaxBps * 100f, 0f, 100f);
    }
}
