using System;
using System.Collections.Generic;

namespace SystemGuardian.Core.Models;

/// <summary>
/// ForecastResult: 30-second forward projection for each resource by AGENT-02
/// Producer: AGENT-02 (Forecasting Agent)
/// Consumers: Orchestrator (tier decisions), AGENT-09 (UI display), AGENT-08 (logging)
/// </summary>
public class ForecastResult
{
    public string ForecastId { get; set; } = Guid.NewGuid().ToString();
    public string TickId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public Dictionary<string, ResourceForecast> Resources { get; set; } = new();
    public string WorstResource { get; set; }
    public int RecommendedTier { get; set; } // 1-4
    public int BufferSizeUsed { get; set; }
    public List<string> ModelErrors { get; set; } = new();

    public class ResourceForecast
    {
        public float CurrentPct { get; set; }
        public float ProjectedPct { get; set; }
        public string Trend { get; set; } // "rising", "falling", "stable"
        public float Confidence { get; set; } // 0.0 - 1.0
        public bool WillBreach { get; set; }
        public int? BreachEtaSec { get; set; } // Seconds until breach
    }
}
