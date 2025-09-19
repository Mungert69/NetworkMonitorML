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

        var rtts = inputs.Select(x => (double)x.RoundTripTime).ToArray();
        var n = rtts.Length;

        var preds = new List<AnomalyPrediction>(n);
        for (int i = 0; i < Math.Min(PreTrain, n); i++)
            preds.Add(new AnomalyPrediction { Prediction = new double[] { 0, 0, 0.5, 0 } });

        if (n <= PreTrain)
            return preds;

        // rolling prefixes: horizon=1
        var batchSeries = new List<List<double>>(n - PreTrain);
        for (int i = PreTrain; i < n; i++)
            batchSeries.Add(rtts.Take(i).ToList());

        var payloadJson = JsonSerializer.Serialize(new
        {
            model = "google/timesfm-2.5-200m-pytorch",
            messages = new object[]
            {
                new { role = "system", content = "You are a time-series forecaster." },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        series = batchSeries,
                        horizon = 1,
                        quantiles = true,
                        max_context = 4096
                    })
                }
            }
        });

        var content = ReadSingleAssistantContentAsync(payloadJson).GetAwaiter().GetResult();
        var resp = JsonSerializer.Deserialize<TimesFmResponse>(content)
                   ?? throw new InvalidOperationException("TimesFM: null response");

        // Robust normalization
        var f = NormalizeForecast(resp);         // double[] length >=1 or B
        var q = NormalizeQuantiles(resp);        // double?[][] or null

        int B = batchSeries.Count;

        double ForecastAt(int j)
        {
            if (f.Length == 0) return double.NaN;
            if (j < f.Length) return f[j];
            if (f.Length == 1) return f[0];      // broadcast single forecast
            return f[^1];                        // fallback to last
        }

        double?[]? QuantRowAt(int j)
        {
            if (q == null || q.Length == 0) return null;
            if (j < q.Length) return q[j];
            if (q.Length == 1) return q[0];      // broadcast single row
            return null;
        }

        var (loIdx, hiIdx) = PickQuantileIndices(Confidence);

        for (int i = PreTrain; i < n; i++)
        {
            var j = i - PreTrain;
            var y = rtts[i];
            var yhat = ForecastAt(j);

            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            var row = QuantRowAt(j);
            if (row != null && row.Length >= 10)
            {
                lo = (loIdx < row.Length && row[loIdx].HasValue) ? row[loIdx]!.Value : double.NegativeInfinity;
                hi = (hiIdx < row.Length && row[hiIdx].HasValue) ? row[hiIdx]!.Value : double.PositiveInfinity;
            }

            var score = Math.Abs(y - yhat);
            var outside = (y < lo) || (y > hi);
            var p = outside ? TailP(loIdx, hiIdx) : 0.5;

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
        if (confidence >= 0.80) return (1, 9); // q10..q90
        if (confidence >= 0.60) return (2, 8); // q20..q80
        if (confidence >= 0.40) return (3, 7); // q30..q70
        if (confidence >= 0.20) return (4, 6); // q40..q60
        return (5, 5); // median only
    }

    private static double TailP(int loIdx, int hiIdx)
    {
        var central = (hiIdx - loIdx) * 0.10; // tens quantiles
        var tail = Math.Max(0.0, Math.Min(0.5, (1.0 - central) / 2.0));
        return tail <= 0 ? 0.5 : tail;
    }

    // Accepts [v] or [[v],...]
    private static double[] NormalizeForecast(TimesFmResponse r)
    {
        if (r.Forecast is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            if (el.GetArrayLength() == 0) return Array.Empty<double>();

            var first = el[0];
            if (first.ValueKind == JsonValueKind.Array)
            {
                // [[v], [v], ...]
                return el.EnumerateArray().Select(e =>
                {
                    var inner = e.EnumerateArray();
                    return inner.MoveNext() ? inner.Current.GetDouble() : double.NaN;
                }).ToArray();
            }
            if (first.ValueKind == JsonValueKind.Number)
            {
                // [v]
                return el.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            }
        }
        throw new InvalidOperationException("TimesFM: unknown forecast shape");
    }

    // Accepts [10], [[10]], or BxHx10 (take H=1). Returns per-batch row or null.
    private static double?[][]? NormalizeQuantiles(TimesFmResponse r)
    {
        if (r.Quantiles is null) return null;
        if (r.Quantiles is not JsonElement el) return null;
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() == 0) return null;

        // [10]
        if (el[0].ValueKind == JsonValueKind.Number)
        {
            var row = el.EnumerateArray().Select(q => (double?)q.GetDouble()).ToArray();
            return new[] { row };
        }

        // [[10]]
        if (el[0].ValueKind == JsonValueKind.Array && el[0].GetArrayLength() > 0 && el[0][0].ValueKind == JsonValueKind.Number)
        {
            return el.EnumerateArray()
                     .Select(rowEl => rowEl.EnumerateArray().Select(q => (double?)q.GetDouble()).ToArray())
                     .ToArray();
        }

        // B x H x 10 -> pick H=1
        if (el[0].ValueKind == JsonValueKind.Array && el[0].GetArrayLength() > 0 && el[0][0].ValueKind == JsonValueKind.Array)
        {
            return el.EnumerateArray()
                     .Select(b =>
                     {
                         if (b.ValueKind != JsonValueKind.Array || b.GetArrayLength() == 0) return Array.Empty<double?>();
                         var h0 = b[0];
                         return h0.ValueKind == JsonValueKind.Array
                             ? h0.EnumerateArray().Select(q => (double?)q.GetDouble()).ToArray()
                             : Array.Empty<double?>();
                     })
                     .ToArray();
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
