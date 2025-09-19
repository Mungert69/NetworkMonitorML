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

    // --- knobs ---
    const int SAMPLE_ROWS = 6;          // first 4 + last 2 rows logged
    const double NEAR_MISS_FRAC = 0.10; // within 10% of band edge
    const bool LOG_JSON = true;         // structured JSON line per near-miss/outside

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

    var f = NormalizeForecast(resp);          // double[] length >=1 or B
    var q = NormalizeQuantiles(resp);         // double?[][] or null

    double ForecastAt(int j)
    {
        if (f.Length == 0) return double.NaN;
        if (j < f.Length) return f[j];
        if (f.Length == 1) return f[0];
        return f[^1];
    }
    double?[]? QuantRowAt(int j)
    {
        if (q == null || q.Length == 0) return null;
        if (j < q.Length) return q[j];
        if (q.Length == 1) return q[0];
        return null;
    }

    var (loIdx, hiIdx) = PickQuantileIndices(Confidence);

    int near = 0, outsideCnt = 0;
    double maxResid = 0, minMargin = double.PositiveInfinity;
    var samples = new List<string>(SAMPLE_ROWS);

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

        // diagnostics
        double bandW = (double.IsFinite(lo) && double.IsFinite(hi)) ? (hi - lo) : double.NaN;
        double margin = double.IsFinite(bandW)
            ? (y < lo ? (y - lo) : (y > hi ? (hi - y) : Math.Min(y - lo, hi - y)))
            : double.NaN;
        double fracToEdge = (double.IsFinite(bandW) && bandW > 0 && double.IsFinite(margin))
            ? Math.Max(0, margin) / bandW
            : double.NaN;

        maxResid = Math.Max(maxResid, score);
        if (double.IsFinite(margin)) minMargin = Math.Min(minMargin, margin);
        if (outside) outsideCnt++;
        else if (double.IsFinite(fracToEdge) && fracToEdge <= NEAR_MISS_FRAC) near++;

        // sample: first 4 and last 2 rows
        if (samples.Count < 4 || j >= (n - PreTrain) - 2)
        {
            if (LOG_JSON)
            {
                var obj = new
                {
                    model = "timesfm",
                    monitor = _monitorPingInfoID,
                    j,
                    y,
                    yhat,
                    lo = double.IsFinite(lo) ? lo : (double?)null,
                    hi = double.IsFinite(hi) ? hi : (double?)null,
                    resid = score,
                    margin = double.IsFinite(margin) ? margin : (double?)null,
                    width = double.IsFinite(bandW) ? bandW : (double?)null,
                    frac = double.IsFinite(fracToEdge) ? fracToEdge : (double?)null,
                    flag = outside ? "OUT" : (double.IsFinite(fracToEdge) && fracToEdge <= NEAR_MISS_FRAC ? "NEAR" : "OK")
                };
                samples.Add(JsonSerializer.Serialize(obj));
            }
            else
            {
                samples.Add(
                    $"#{j} y={y:0.###} ŷ={yhat:0.###} lo={(double.IsFinite(lo)?lo:double.NaN):0.###} hi={(double.IsFinite(hi)?hi:double.NaN):0.###} " +
                    $"resid={score:0.###} margin={(double.IsFinite(margin)?margin:double.NaN):0.###} width={(double.IsFinite(bandW)?bandW:double.NaN):0.###} " +
                    $"{(outside ? "OUT" : (double.IsFinite(fracToEdge) && fracToEdge <= NEAR_MISS_FRAC ? "NEAR" : "OK"))}"
                );
            }
        }

        preds.Add(new AnomalyPrediction
        {
            Prediction = new[] { outside ? 1d : 0d, score, p, 0d }
        });
    }

    // single-line summary
    if (_log.IsEnabled(LogLevel.Information))
    {
        int B = n - PreTrain;
        _log.LogInformation(
            "timesfm summary monitor={Monitor} B={B} conf={Conf:0.##} band={LoPct}%..{HiPct}% outside={Outside} near={Near} maxResid={Max:0.###} minMargin={Min:0.###}",
            _monitorPingInfoID, B, Confidence, loIdx * 10, hiIdx * 10, outsideCnt, near,
            maxResid, double.IsInfinity(minMargin) ? double.NaN : minMargin
        );
        // sample lines
        foreach (var line in samples)
            _log.LogInformation("timesfm sample {Line}", line);
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
