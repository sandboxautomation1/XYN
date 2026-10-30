using Newtonsoft.Json;

namespace XYN.Signal;

public class SignalResponse
{
    [JsonProperty("symbol")] public string Symbol { get; set; } = "";
    [JsonProperty("timeframe")] public string Timeframe { get; set; } = "";
    [JsonProperty("generated_at")] public DateTime GeneratedAt { get; set; }
    [JsonProperty("action")] public string Action { get; set; } = "";
    [JsonProperty("confidence_label")] public string ConfidenceLabel { get; set; } = "";
    [JsonProperty("signal_summary")] public string SignalSummary { get; set; } = "";
    [JsonProperty("bias_direction")] public string BiasDirection { get; set; } = "";
    [JsonProperty("bias_score")] public int BiasScore { get; set; }
    [JsonProperty("confidence_components")] public List<ConfidenceComponent> ConfidenceComponents { get; set; } = new();
    [JsonProperty("total_confidence")] public int TotalConfidence { get; set; }
    [JsonProperty("reasoning_summary")] public string ReasoningSummary { get; set; } = "";
    [JsonProperty("bullish_factors")] public List<Factor> BullishFactors { get; set; } = new();
    [JsonProperty("bearish_factors")] public List<Factor> BearishFactors { get; set; } = new();
    [JsonProperty("risk_factors")] public List<Factor> RiskFactors { get; set; } = new();
    [JsonProperty("trade_levels")] public TradeLevels TradeLevels { get; set; } = new();
    [JsonProperty("pattern_count")] public int PatternCount { get; set; }
    [JsonProperty("language")] public string Language { get; set; } = "";
    [JsonProperty("translation_status")] public string TranslationStatus { get; set; } = "";
}

public class ConfidenceComponent
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("score")] public double Score { get; set; }
    [JsonProperty("max_score")] public double MaxScore { get; set; }
    [JsonProperty("weight_percent")] public int WeightPercent { get; set; }
    [JsonProperty("details")] public string Details { get; set; } = "";
}

public class Factor
{
    [JsonProperty("label")] public string Label { get; set; } = "";
    [JsonProperty("explanation")] public string Explanation { get; set; } = "";
    [JsonProperty("strength")] public int Strength { get; set; }
}

public class TradeLevels
{
    [JsonProperty("entry")] public double Entry { get; set; }
    [JsonProperty("target")] public double Target { get; set; }
    [JsonProperty("stop_loss")] public double StopLoss { get; set; }
    [JsonProperty("risk_reward_ratio")] public double RiskRewardRatio { get; set; }
}
