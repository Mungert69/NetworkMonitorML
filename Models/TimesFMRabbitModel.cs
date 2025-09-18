// File: ML/TimesFmRabbitModel.cs
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NetworkMonitor.ML.Model;
using NetworkMonitor.Objects.Repository;   // IRabbitRepo, RabbitTransport
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using NetworkMonitor.Objects;
namespace NetworkMonitor.ML.Model;

public sealed class TimesFmRabbitModel : IMLModel, IDisposable
{
    private readonly RabbitTransport _tx;
    private readonly ILogger<TimesFmRabbitModel> _log;
    private readonly string _routingKey;
    private readonly int _monitorPingInfoID;

    public double Confidence { get; set; }
    public int PreTrain { get; set; }

    public TimesFmRabbitModel(
        IRabbitRepo rabbitRepo,
        SystemUrl sys,
        ILogger<TimesFmRabbitModel> log,
        int monitorPingInfoID,
        double confidence,
        int preTrain,
        string routingKey)
    {
        _tx = new RabbitTransport(rabbitRepo, sys, routingKey, log);
        _log = log;
        _routingKey = routingKey;
        _monitorPingInfoID = monitorPingInfoID;
        Confidence = confidence;
        PreTrain = preTrain;
    }

    public void Train(List<LocalPingInfo> data) { /* no-op */ }

    public AnomalyPrediction Predict(LocalPingInfo input)
        => PredictList(new List<LocalPingInfo> { input }).First();

    public IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs)
    {
        if (inputs == null || inputs.Count == 0)
            return Array.Empty<AnomalyPrediction>();

        // 1) Build rolling prefix batch for horizon=1
        var rtts = inputs.Select(x => (double)x.RoundTripTime).ToArray();
        var n = rtts.Length;

        var preds = new List<AnomalyPrediction>(n);
        for (int i = 0; i < Math.Min(PreTrain, n); i++)
            preds.Add(new AnomalyPrediction { Prediction = new double[] { 0, 0, 0.5, 0 } });

        if (n <= PreTrain)
            return preds;

        var batchSeries = new List<List<double>>(n - PreTrain);
        for (int i = PreTrain; i < n; i++)
            batchSeries.Add(rtts.Take(i).ToList());

        // 2) Send one OpenAI-style chat request carrying JSON {series:[...], horizon:1, quantiles:true}
        var payloadJson = JsonSerializer.Serialize(new
        {
            model = "timesfm-2.5",
            messages = new object[]
            {
                new { role = "system", content = "You are a time-series forecaster." },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        series = batchSeries,   // batch of prefixes
                        horizon = 1,
                        quantiles = true,
                        max_context = 4096      // cap context
                    })
                }
            }
        });

        var content = ReadSingleAssistantContentAsync(payloadJson).GetAwaiter().GetResult();
        var resp = JsonSerializer.Deserialize<TimesFmResponse>(content)
                   ?? throw new InvalidOperationException("TimesFM: null response");

        // normalize forecast/quantiles shapes
        var f = NormalizeForecast(resp);
        var q = NormalizeQuantiles(resp); // may be null

        // 3) Map to anomaly vector per i >= PreTrain
        var (loIdx, hiIdx) = PickQuantileIndices(Confidence);
        for (int i = PreTrain; i < n; i++)
        {
            var j = i - PreTrain;
            var y = rtts[i];
            var yhat = f[j];                       // horizon=1
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;

            if (q != null && q[j] != null && q[j]!.Length >= 10)
            {
                // quantiles layout: [mean, q10, q20, ..., q90]
                var arr = q[j]!;
                lo = (loIdx < arr.Length ? (arr[loIdx] ?? double.NegativeInfinity) : double.NegativeInfinity);
                hi = (hiIdx < arr.Length ? (arr[hiIdx] ?? double.PositiveInfinity) : double.PositiveInfinity);
            }

            var score = Math.Abs(y - yhat);
            var outside = (y < lo) || (y > hi);
            var p = outside ? TailP(loIdx, hiIdx) : 0.5; // coarse proxy from band width
            preds.Add(new AnomalyPrediction
            {
                Prediction = new[] { outside ? 1d : 0d, score, p, 0d }
            });
        }

        return preds;
    }

    public void PrintPrediction(IEnumerable<AnomalyPrediction> predictions)
    {
        var sb = new StringBuilder();
        sb.Append($"[{_monitorPingInfoID}] TimesFM preds: ");
        int i = 0;
        foreach (var p in predictions.Take(5))
        {
            var v = p.Prediction;
            sb.Append($"#{i++} a={v[0]} s={v[1]:0.###} p={v[2]:0.###}; ");
        }
        _log.LogInformation(sb.ToString());
    }

    public void Dispose() => _tx?.Dispose();

    // ---------- helpers ----------

    private async Task<string> ReadSingleAssistantContentAsync(string openAiChatRequestJson, CancellationToken ct = default)
    {
        var requestObj = JsonSerializer.Deserialize<object>(openAiChatRequestJson)!;

        var sb = new StringBuilder();
        await foreach (var chunkJson in _tx.CreateChatCompletionStreamAsync(requestObj, ct))
        {
            if (chunkJson == "__STREAM_END__") break;

            try
            {
                using var doc = JsonDocument.Parse(chunkJson);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var contentEl))
                {
                    var part = contentEl.GetString();
                    if (!string.IsNullOrEmpty(part)) sb.Append(part);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chunk parse failed");
            }
        }
        return sb.ToString();
    }

    private static (int loIdx, int hiIdx) PickQuantileIndices(double confidence)
    {
        // Map to available tens: [mean, q10..q90]
        if (confidence >= 0.80) return (1, 9); // q10..q90
        if (confidence >= 0.60) return (2, 8); // q20..q80
        if (confidence >= 0.40) return (3, 7); // q30..q70
        if (confidence >= 0.20) return (4, 6); // q40..q60
        return (5, 5); // median only
    }

    private static double TailP(int loIdx, int hiIdx)
    {
        // q10..q90 => 0.10 per tail; q20..q80 => 0.20 per tail, etc.
        var central = (hiIdx - loIdx) * 0.10;     // width in probability units
        var tail = Math.Max(0.0, Math.Min(0.5, (1.0 - central) / 2.0));
        return tail <= 0 ? 0.5 : tail;
    }

    private static double[] NormalizeForecast(TimesFmResponse r)
    {
        // cases:
        //  - forecast: [v]                        (single series, H=1)
        //  - forecast: [[v], [v], ...]           (batch B, H=1)
        if (r.Forecast is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                // batch?
                if (el.GetArrayLength() > 0 && el[0].ValueKind == JsonValueKind.Array)
                    return el.EnumerateArray().Select(e => e[0].GetDouble()).ToArray();
                // single
                return el.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            }
        }
        throw new InvalidOperationException("TimesFM: unknown forecast shape");
    }

    // Replace NormalizeQuantiles with this version
    private static double?[][]? NormalizeQuantiles(TimesFmResponse r)
    {
        if (r.Quantiles is null) return null;
        if (r.Quantiles is not JsonElement el) return null;
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() == 0) return null;

        var first = el[0];
        if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() == 0) return null;

        var firstInner = first[0];

        // Case A: B x H x 10  (batch)
        if (firstInner.ValueKind == JsonValueKind.Array)
        {
            return el.EnumerateArray()
                     .Select(b =>
                         (b.ValueKind == JsonValueKind.Array && b.GetArrayLength() > 0)
                         ? b[0].EnumerateArray().Select(q => (double?)q.GetDouble()).ToArray() // pick H=1
                         : Array.Empty<double?>())
                     .ToArray();
        }

        // Case B: H x 10 (single-series unwrapped); pick H=1 row
        if (firstInner.ValueKind == JsonValueKind.Number)
        {
            var row = first.EnumerateArray().Select(q => (double?)q.GetDouble()).ToArray();
            return new[] { row };
        }

        return null;
    }


    // ---------- DTO ----------
    private sealed class TimesFmResponse
    {
        [JsonPropertyName("forecast")] public object? Forecast { get; init; }
        [JsonPropertyName("quantiles")] public object? Quantiles { get; init; }
        [JsonPropertyName("horizon")] public int Horizon { get; init; }
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("backend")] public string? Backend { get; init; }
    }
}
