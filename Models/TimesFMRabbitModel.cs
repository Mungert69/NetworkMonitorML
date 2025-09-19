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

    // ---- Sensitivity + Adaptation knobs ----
    // Persistence (temporal gating)
    private const int RUN_LEN = 3;       // require ≥3 consecutive outsides
    private const int K_OF_N_K = 6;      // or ≥6 outsides within last N
    private const int K_OF_N_N = 12;     // window for k-of-n

    // Band calibration (jitter-aware + level-aware)
    private const double MAD_ALPHA = 1.0;     // widen band by +/- MAD_ALPHA * sigma
    private const double MIN_BAND_ABS = 5.0;  // absolute min width (ms)
    private const double MIN_BAND_REL = 0.15; // relative min width vs |yhat|

    // Rolling calibration (lets sigma follow regime shifts)
    private const int ROLL_SIGMA_WIN = 60;    // samples to compute rolling sigma
    private const int BASELINE_WIN = 120;   // samples for rolling baseline (median/MAD)

    // Post-alert behavior
    private const int SIGMA_COOLDOWN = 30;   // freeze sigma for N samples after CHANGE
    private const double MIN_REL_SHIFT = 0.20; // require ≥20% shift vs baseline to call CHANGE

    // Logging
    private const int SAMPLE_ROWS = 6;          // first 4 + last 2 rows logged
    private const double NEAR_MISS_FRAC = 0.10; // within 10% of band edge
    private const bool LOG_JSON = true;         // structured JSON for diagnostics

    // State for cooldown/rolling sigma
    private int _cooldown = 0;
    private double _lastSigma = 1.0;

    // ---- Martingale evidence accumulator (legacy parity in slot [3]) ----
    private double _martingale = 1.0;
    private const double MART_EPS = 0.92;    // 0<eps<1; smaller => stronger boost
    private const double MART_CLAMP = 1e6;     // safety bound
    private double _maxMartingaleThisBatch = 1.0;

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
            preds.Add(new AnomalyPrediction { Prediction = new double[] { 0, 0, 0.5, 1.0 } });

        if (n <= PreTrain)
            return preds;

        // Reset per-batch martingale max
        _maxMartingaleThisBatch = Math.Max(_maxMartingaleThisBatch, _martingale);

        // Rolling prefixes: horizon=1
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
                        // if backend supports: quantile_crossing_fix=true
                    })
                }
            }
        });

        var content = ReadSingleAssistantContentAsync(payloadJson).GetAwaiter().GetResult();
        var resp = JsonSerializer.Deserialize<TimesFmResponse>(content)
                   ?? throw new InvalidOperationException("TimesFM: null response");

        var f = NormalizeForecast(resp);      // length >=1
        var q = NormalizeQuantiles(resp);     // double?[][] or null

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

        // Seed sigma from pretrain
        _lastSigma = RobustSigma(rtts.Take(PreTrain));

        int near = 0, outsideCnt = 0, flaggedCnt = 0;
        double maxResid = 0, minMargin = double.PositiveInfinity;
        var samples = new List<string>(SAMPLE_ROWS);

        int runLen = 0;
        var kOfNQueue = new Queue<bool>(K_OF_N_N);
        int kOfNCount = 0;

        for (int i = PreTrain; i < n; i++)
        {
            var j = i - PreTrain;
            var y = rtts[i];
            var yhat = ForecastAt(j);

            // --- Rolling baseline (for magnitude gate + observability) ---
            int bStart = Math.Max(0, i - BASELINE_WIN);
            var baseWin = rtts.Skip(bStart).Take(i - bStart).ToArray();
            double baselineMed = baseWin.Length > 0 ? Median(baseWin) : yhat;
            double baselineMad = baseWin.Length > 0
                ? 1.4826 * Median(baseWin.Select(v => Math.Abs(v - baselineMed)).ToArray())
                : 0.0;

            // --- Rolling sigma with cooldown (for band inflation) ---
            double sigma;
            if (_cooldown > 0)
            {
                sigma = _lastSigma;
                _cooldown--;
            }
            else
            {
                int sStart = Math.Max(0, i - ROLL_SIGMA_WIN);
                sigma = RobustSigma(rtts.Skip(sStart).Take(i - sStart));
                _lastSigma = sigma;
            }

            // --- Base band from quantiles (q10..q90 on 1..9; index 0 ~ mean) ---
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            var row = QuantRowAt(j);
            if (row != null && row.Length >= 10)
            {
                lo = (loIdx < row.Length && row[loIdx].HasValue) ? row[loIdx]!.Value : double.NegativeInfinity;
                hi = (hiIdx < row.Length && row[hiIdx].HasValue) ? row[hiIdx]!.Value : double.PositiveInfinity;
            }

            // Fallback if missing quantiles
            if (!double.IsFinite(lo) || !double.IsFinite(hi))
            {
                lo = yhat - 3 * sigma;
                hi = yhat + 3 * sigma;
            }

            // Inflate band by robust noise
            lo -= MAD_ALPHA * sigma;
            hi += MAD_ALPHA * sigma;

            // Enforce minimum width (protect from razor-thin bands)
            double w = hi - lo;
            double minW = Math.Max(MIN_BAND_ABS, MIN_BAND_REL * Math.Max(1.0, Math.Abs(yhat)));
            if (!double.IsFinite(w) || w < minW)
            {
                double half = 0.5 * minW;
                lo = yhat - half;
                hi = yhat + half;
                w = minW;
            }

            // --- Deviation metrics ---
            bool outside = (y < lo) || (y > hi);
            double resid = Math.Abs(y - yhat);
            double normResid = resid / Math.Max(1e-9, w);

            // --- Persistence gates ---
            runLen = outside ? runLen + 1 : 0;

            if (kOfNQueue.Count == K_OF_N_N)
            {
                if (kOfNQueue.Dequeue()) kOfNCount--;
            }
            kOfNQueue.Enqueue(outside);
            if (outside) kOfNCount++;

            bool persistenceHit = (runLen >= RUN_LEN) || (kOfNCount >= K_OF_N_K);

            // --- Relative magnitude gate (vs baseline) ---
            double relShift = Math.Abs(yhat - baselineMed) / Math.Max(1.0, Math.Abs(baselineMed));
            bool bigShift = relShift >= MIN_REL_SHIFT;

            bool martingaleHit = _martingale >= 150.0; // start here; tune later
            bool changeFlag = persistenceHit && (bigShift || martingaleHit);

            // cooldown kicks in on the first confirmed change
            if (changeFlag && _cooldown == 0)
                _cooldown = SIGMA_COOLDOWN;

            // --- p-values & martingale ---
            var baseTail = TailP(loIdx, hiIdx);

            // p for *output*: smaller when persistent/confirmed; neutral 0.5 when inside
            double pOut = changeFlag
                ? Math.Max(1e-6, baseTail * Math.Pow(0.5, Math.Max(0, runLen - RUN_LEN + 1)))
                : (outside ? baseTail : 0.5);

            // p for *martingale*: reflect surprise of this single point only
            double pMart = outside ? baseTail : 0.5;
            double evid = (pMart <= 0.5) ? (1.0 - pMart) : pMart; // map to [0.5,1]
            evid = Math.Min(Math.Max(evid, 1e-6), 1.0 - 1e-6);

            _martingale *= (MART_EPS * Math.Pow(evid, MART_EPS - 1.0));
            if (_martingale > MART_CLAMP) _martingale = MART_CLAMP;
            if (_martingale > _maxMartingaleThisBatch) _maxMartingaleThisBatch = _martingale;

            // --- Diagnostics ---
            double margin = outside
                ? (y < lo ? y - lo : hi - y)   // negative when outside
                : Math.Min(y - lo, hi - y);
            double fracToEdge = Math.Max(0, margin) / w;

            maxResid = Math.Max(maxResid, resid);
            if (double.IsFinite(margin)) minMargin = Math.Min(minMargin, margin);
            if (outside) outsideCnt++;
            if (!outside && double.IsFinite(fracToEdge) && fracToEdge <= NEAR_MISS_FRAC) near++;
            if (changeFlag) flaggedCnt++;

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
                        lo,
                        hi,
                        resid,
                        normResid,
                        sigma,
                        width = w,
                        runLen,
                        kOfN = new { k = K_OF_N_K, n = K_OF_N_N, count = kOfNCount },
                        baseline = new { median = baselineMed, mad = baselineMad, relShift, minRelShift = MIN_REL_SHIFT },
                        gates = new
                        {
                            outside,
                            persistence = persistenceHit,
                            bigShift,
                            cooldown = _cooldown
                        },
                        p = new { output = pOut, martingale_p = pMart },
                        martingale = _martingale,
                        flag = changeFlag ? "CHANGE" : (outside ? "OUT" : (fracToEdge <= NEAR_MISS_FRAC ? "NEAR" : "OK"))
                    };
                    samples.Add(JsonSerializer.Serialize(obj));
                }
                else
                {
                    samples.Add(
                        $"#{j} y={y:0.###} ŷ={yhat:0.###} lo={lo:0.###} hi={hi:0.###} " +
                        $"resid={resid:0.###} norm={normResid:0.###} σ={sigma:0.###} w={w:0.###} " +
                        $"run={runLen} kOfN={K_OF_N_K}/{K_OF_N_N}={kOfNCount} " +
                        $"base≈{baselineMed:0.###} relShift={relShift:0.###} pOut={pOut:0.###} " +
                        $"M={_martingale:0.###} {(changeFlag ? "CHANGE" : (outside ? "OUT" : "OK"))} cd={_cooldown}"
                    );
                }
            }

            preds.Add(new AnomalyPrediction
            {
                // [ alert, score, p, martingale ]
                // alert -> persistence & %-shift
                // score -> normalized residual (unitless)
                // p -> detection-side p (with persistence shaping)
                // martingale -> cumulative evidence (legacy parity)
                Prediction = new[] { changeFlag ? 1d : 0d, normResid, pOut, _martingale }
            });
        }

        if (_log.IsEnabled(LogLevel.Information))
        {
            int B = n - PreTrain;
            _log.LogInformation(
                "timesfm summary monitor={Monitor} B={B} conf={Conf:0.##} band={Lo}%..{Hi}% outside={Outside} flagged={Flagged} near={Near} maxResid={Max:0.###} minMargin={Min:0.###} coolDown={Cooldown} maxM={MaxM:0.###}",
                _monitorPingInfoID, B, Confidence, loIdx * 10, hiIdx * 10,
                outsideCnt, flaggedCnt, near, maxResid, double.IsInfinity(minMargin) ? double.NaN : minMargin, _cooldown, _maxMartingaleThisBatch
            );
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
            sb.Append($"#{i++} a={v[0]} s={v[1]:0.###} p={v[2]:0.###} M={v[3]:0.###}; ");
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
        // tens quantiles on indices 1..9 (index 0 == mean)
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

        if (el[0].ValueKind == JsonValueKind.Number)
        {
            var row = el.EnumerateArray().Select(q => (double?)q.GetDouble()).ToArray();
            return new[] { row };
        }

        if (el[0].ValueKind == JsonValueKind.Array && el[0].GetArrayLength() > 0 && el[0][0].ValueKind == JsonValueKind.Number)
        {
            return el.EnumerateArray()
                     .Select(rowEl => rowEl.EnumerateArray().Select(q => (double?)q.GetDouble()).ToArray())
                     .ToArray();
        }

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

    private static double RobustSigma(IEnumerable<double> xs)
    {
        var arr = xs.ToArray();
        if (arr.Length == 0) return 1.0;
        double med = Median(arr);
        var devs = arr.Select(v => Math.Abs(v - med)).ToArray();
        double mad = Median(devs);
        double sigma = 1.4826 * (mad <= 1e-9 ? Std(arr) : mad);
        return Math.Max(sigma, 1e-6);
    }

    private static double Median(IList<double> a)
    {
        var b = a.ToArray();
        Array.Sort(b);
        int m = b.Length / 2;
        return (b.Length % 2 == 1) ? b[m] : 0.5 * (b[m - 1] + b[m]);
    }

    private static double Std(IList<double> a)
    {
        double mean = a.Average();
        double v = a.Select(x => (x - mean) * (x - mean)).Average();
        return Math.Sqrt(Math.Max(v, 0));
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
