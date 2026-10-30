using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;


namespace XYN.Signal
{

    public class SignalResponse
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        [JsonPropertyName("timeframe")]
        public string Timeframe { get; set; } = "";

        [JsonPropertyName("generated_at")]
        public DateTime GeneratedAt { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; } = "";

        [JsonPropertyName("confidence_label")]
        public string ConfidenceLabel { get; set; } = "";

        [JsonPropertyName("signal_summary")]
        public string SignalSummary { get; set; } = "";

        [JsonPropertyName("bias_direction")]
        public string BiasDirection { get; set; } = "";

        [JsonPropertyName("bias_score")]
        public int BiasScore { get; set; }

        [JsonPropertyName("confidence_components")]
        public List<ConfidenceComponent> ConfidenceComponents { get; set; } = new();

        [JsonPropertyName("total_confidence")]
        public int TotalConfidence { get; set; }

        [JsonPropertyName("reasoning_summary")]
        public string ReasoningSummary { get; set; } = "";

        [JsonPropertyName("bullish_factors")]
        public List<Factor> BullishFactors { get; set; } = new();

        [JsonPropertyName("bearish_factors")]
        public List<Factor> BearishFactors { get; set; } = new();

        [JsonPropertyName("risk_factors")]
        public List<Factor> RiskFactors { get; set; } = new();

        [JsonPropertyName("trade_levels")]
        public TradeLevels TradeLevels { get; set; } = new();

        [JsonPropertyName("pattern_count")]
        public int PatternCount { get; set; }

        [JsonPropertyName("language")]
        public string Language { get; set; } = "";

        [JsonPropertyName("translation_status")]
        public string TranslationStatus { get; set; } = "";
    }

    public class ConfidenceComponent
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("score")]
        public double Score { get; set; }

        [JsonPropertyName("max_score")]
        public double MaxScore { get; set; }

        [JsonPropertyName("weight_percent")]
        public int WeightPercent { get; set; }

        [JsonPropertyName("details")]
        public string Details { get; set; } = "";
    }

    public class Factor
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; } = "";

        [JsonPropertyName("strength")]
        public int Strength { get; set; }
    }

    public class TradeLevels
    {
        [JsonPropertyName("entry")]
        public double Entry { get; set; }

        [JsonPropertyName("target")]
        public double Target { get; set; }

        [JsonPropertyName("stop_loss")]
        public double StopLoss { get; set; }

        [JsonPropertyName("risk_reward_ratio")]
        public double RiskRewardRatio { get; set; }
    }
}