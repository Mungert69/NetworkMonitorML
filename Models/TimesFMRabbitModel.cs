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
    private readonly string _modelType;

    public double Confidence { get; set; }
    public int PreTrain { get; set; }

    // ---- Sensitivity + Adaptation knobs (configurable) ----
    private TimesFmResolvedSettings _settings = new();
    private int _runLen;
    private int _kOfNK;
    private int _kOfNN;
    private double _madAlpha;
    private double _minBandAbs;
    private double _minBandRel;
    private int _rollSigmaWin;
    private int _baselineWin;
    private int _sigmaCooldownSetting;
    private double _minRelShift;
    private int _sampleRows;
    private double _nearMissFrac;
    private bool _logJson = true;

    private static readonly TimeSpan LlmStreamTimeout = TimeSpan.FromMinutes(3);

    // State for cooldown/rolling sigma
    private int _cooldown = 0;
    private double _lastSigma = 1.0;

    // ---- Martingale evidence accumulator (slot [3]) ----
    // Telemetry style: nearly neutral on calm points; climbs near/over band edges.
    private double _martingale = 1.0;
    private const double MART_EPS = 0.997;   // gentler power-martingale (flatter in calm)
    private const double MART_CLAMP = 1e6;     // safety bound
    private double _maxMartingaleThisBatch = 1.0;

    public TimesFmRabbitModel(
        IRabbitRepo rabbitRepo,
        SystemUrl sys,
        ILogger<TimesFmRabbitModel> log,
        int monitorPingInfoID,
        double confidence,
        int preTrain,
        string modelType,
        string routingKey,
        TimesFmResolvedSettings? settings = null)
    {
        _tx = new RabbitTransport(rabbitRepo, sys, routingKey, log);
        _log = log;
        _routingKey = routingKey;
        _monitorPingInfoID = monitorPingInfoID;
        _modelType = string.IsNullOrWhiteSpace(modelType) ? "unknown" : modelType;
        Confidence = confidence;
        PreTrain = preTrain;
        ApplySettings(settings ?? new TimesFmResolvedSettings());
    }

    public void ApplySettings(TimesFmResolvedSettings settings)
    {
        if (settings == null)
            return;

        _settings = new TimesFmResolvedSettings
        {
            RunLength = settings.RunLength,
            KOfNK = settings.KOfNK,
            KOfNN = settings.KOfNN,
            MadAlpha = settings.MadAlpha,
            MinBandAbs = settings.MinBandAbs,
            MinBandRel = settings.MinBandRel,
            RollSigmaWindow = settings.RollSigmaWindow,
            BaselineWindow = settings.BaselineWindow,
            SigmaCooldown = settings.SigmaCooldown,
            MinRelShift = settings.MinRelShift,
            SampleRows = settings.SampleRows,
            NearMissFraction = settings.NearMissFraction,
            LogJson = settings.LogJson
        };

        _runLen = Math.Max(1, _settings.RunLength);
        _kOfNN = Math.Max(1, _settings.KOfNN);
        _kOfNK = Math.Clamp(_settings.KOfNK, 1, _kOfNN);
        _madAlpha = Math.Max(0.0, _settings.MadAlpha);
        _minBandAbs = Math.Max(0.0, _settings.MinBandAbs);
        _minBandRel = Math.Max(0.0, _settings.MinBandRel);
        _rollSigmaWin = Math.Max(1, _settings.RollSigmaWindow);
        _baselineWin = Math.Max(1, _settings.BaselineWindow);
        _sigmaCooldownSetting = Math.Max(0, _settings.SigmaCooldown);
        _minRelShift = Math.Max(0.0, _settings.MinRelShift);
        _sampleRows = Math.Max(0, _settings.SampleRows);
        _nearMissFrac = Math.Clamp(_settings.NearMissFraction, 0.0, 1.0);
        _logJson = _settings.LogJson;

        if (_cooldown > _sigmaCooldownSetting)
            _cooldown = _sigmaCooldownSetting;
    }

    public void Train(List<LocalPingInfo> data) { /* no-op */ }

    public AnomalyPrediction Predict(LocalPingInfo input)
        => PredictList(new List<LocalPingInfo> { input }).First();

    public IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs)
    {
        if (inputs == null || inputs.Count == 0)
            return Array.Empty<AnomalyPrediction>();

        var allPreds = new AnomalyPrediction[inputs.Count];
        for (int i = 0; i < allPreds.Length; i++)
        {
            // Neutral placeholders keep alignment but reset any post-processing streak logic.
            // If you need consecutive-detection logic, implement it before we reinsert timeouts.
            allPreds[i] = AnomalyPrediction.Neutral();
        }

        var goodIndices = new List<int>(inputs.Count);
        var goodValues = new List<double>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            if (!inputs[i].IsTimeout())
            {
                goodIndices.Add(i);
                goodValues.Add(inputs[i].RoundTripTime);
            }
        }

        if (goodIndices.Count == 0)
            return allPreds;

        var rtts = goodValues.ToArray();
        var n = rtts.Length;

        if (n < 2)
        {
            _log.LogDebug("[TimesFM] {ModelType} monitor {MonitorId} skipped: only {Usable} usable points after timeout filtering", _modelType, _monitorPingInfoID, n);
            return allPreds;
        }

        int effectivePreTrain = Math.Min(PreTrain, Math.Max(1, n - 1));
        if (effectivePreTrain <= 0)
            effectivePreTrain = 1;
        if (effectivePreTrain >= n)
        {
            _log.LogDebug("[TimesFM] {ModelType} monitor {MonitorId} skipped: usable points {Usable} <= effective pre-train {Effective}", _modelType, _monitorPingInfoID, n, effectivePreTrain);
            return allPreds;
        }
        if (effectivePreTrain < PreTrain)
        {
            _log.LogDebug("[TimesFM] {ModelType} monitor {MonitorId} reduced pre-train from {PreTrain} to {Effective} because only {Usable} usable points were available", _modelType, _monitorPingInfoID, PreTrain, effectivePreTrain, n);
        }

        // Reset martingale per evaluation window for sane, comparable telemetry
        _martingale = 1.0;
        _maxMartingaleThisBatch = 1.0;

        var preds = new List<AnomalyPrediction>(n);
        for (int i = 0; i < Math.Min(effectivePreTrain, n); i++)
            preds.Add(AnomalyPrediction.Neutral());

        // Rolling prefixes: horizon=1
        var batchSeries = new List<List<double>>(n - effectivePreTrain);
        for (int i = effectivePreTrain; i < n; i++)
            batchSeries.Add(rtts.Take(i).ToList());

        _log.LogDebug(
            "[TimesFM] {ModelType} monitor {MonitorId} sending {SeriesCount} prefixes (last len {LastLen}) via routing key '{RoutingKey}'",
            _modelType,
            _monitorPingInfoID,
            batchSeries.Count,
            batchSeries.Count > 0 ? batchSeries[^1].Count : 0,
            _routingKey);

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

        string content;
        try
        {
            using var cts = new CancellationTokenSource(LlmStreamTimeout);
            content = ReadSingleAssistantContentAsync(payloadJson, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            _log.LogError(ex, "[TimesFM] {ModelType} monitor {MonitorId} request timed out after {Timeout}", _modelType, _monitorPingInfoID, LlmStreamTimeout);
            throw new TimeoutException($"TimesFM streaming response timed out after {LlmStreamTimeout}", ex);
        }
        var resp = ParseTimesFmResponse(content);

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
        var samples = new List<string>(Math.Max(0, _sampleRows));

        int runLen = 0;
        var kOfNQueue = new Queue<bool>(_kOfNN);
        int kOfNCount = 0;

        for (int i = effectivePreTrain; i < n; i++)
        {
            var j = i - PreTrain;
            var y = rtts[i];
            var yhat = ForecastAt(j);

            // --- Rolling baseline (for magnitude gate + observability) ---
            int bStart = Math.Max(0, i - _baselineWin);
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
                int sStart = Math.Max(0, i - _rollSigmaWin);
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
            lo -= _madAlpha * sigma;
            hi += _madAlpha * sigma;

            // Enforce minimum width (protect from razor-thin bands)
            double w = hi - lo;
            double minW = Math.Max(_minBandAbs, _minBandRel * Math.Max(1.0, Math.Abs(yhat)));
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

            if (kOfNQueue.Count == _kOfNN)
            {
                if (kOfNQueue.Dequeue()) kOfNCount--;
            }
            kOfNQueue.Enqueue(outside);
            if (outside) kOfNCount++;

            bool persistenceHit = (runLen >= _runLen) || (kOfNCount >= _kOfNK);

            // --- Relative magnitude gate (vs baseline) ---
            double relShift = Math.Abs(yhat - baselineMed) / Math.Max(1.0, Math.Abs(baselineMed));
            bool bigShift = relShift >= _minRelShift;

            bool changeFlag = persistenceHit && bigShift;

            // cooldown kicks in on the first confirmed change
            if (changeFlag && _cooldown == 0)
                _cooldown = _sigmaCooldownSetting;

            // --- p-values & martingale ---
            var baseTail = TailP(loIdx, hiIdx);

            // p for *output*: smaller when persistent/confirmed; neutral 0.5 when inside
            double pOut = changeFlag
                ? Math.Max(1e-6, baseTail * Math.Pow(0.5, Math.Max(0, runLen - _runLen + 1)))
                : (outside ? baseTail : 0.5);

            // p for *martingale*: dead-zone inside the band for flatter calm behavior.
            // sideDenom = distance from mean to the nearer band edge on the side of y.
            double sideDenom = (y >= yhat)
                ? Math.Max(1e-9, hi - yhat)
                : Math.Max(1e-9, yhat - lo);

            // pos: 0 at mean, 1 at the band edge on that side
            double pos = Math.Abs(y - yhat) / sideDenom;

            // Dead-zone: ignore inner 25% of the band (no evidence update there).
            // Re-scale the remainder so the band edge is still ≈ "1σ" (1.2816).
            double posEff = Math.Max(0.0, pos - 0.25);         // [0, 0.75+] after dead-zone
            double z = (posEff <= 0.0) ? 0.0 : (posEff * 1.2816 / 0.75);

            // Map to “p”: simple exponential tail surrogate (no Phi needed).
            double pMart = Math.Exp(-z);
            pMart = Math.Min(Math.Max(pMart, 1e-6), 1.0 - 1e-6);

            _martingale *= (MART_EPS * Math.Pow(pMart, MART_EPS - 1.0));
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
            if (!outside && double.IsFinite(fracToEdge) && fracToEdge <= _nearMissFrac) near++;
            if (changeFlag) flaggedCnt++;

            // sample: first 4 and last 2 rows
            if (samples.Count < 4 || j >= (n - PreTrain) - 2)
            {
                if (_logJson)
                {
                    var obj = new
                    {
                        model = "timesfm",
                        mode = _modelType,
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
                        kOfN = new { k = _kOfNK, n = _kOfNN, count = kOfNCount },
                        baseline = new { median = baselineMed, mad = baselineMad, relShift, minRelShift = _minRelShift },
                        gates = new
                        {
                            outside,
                            persistence = persistenceHit,
                            bigShift,
                            cooldown = _cooldown
                        },
                        p = new { output = pOut, martingale_p = pMart },
                        martingale = _martingale,
                        flag = changeFlag ? "CHANGE" : (outside ? "OUT" : (fracToEdge <= _nearMissFrac ? "NEAR" : "OK"))
                    };
                    samples.Add(JsonSerializer.Serialize(obj));
                }
                else
                {
                    samples.Add(
                        $"#{j} y={y:0.###} ŷ={yhat:0.###} lo={lo:0.###} hi={hi:0.###} " +
                        $"resid={resid:0.###} norm={normResid:0.###} σ={sigma:0.###} w={w:0.###} " +
                        $"run={runLen} kOfN={_kOfNK}/{_kOfNN}={kOfNCount} " +
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
                // martingale -> cumulative evidence (telemetry)
                Prediction = new[] { changeFlag ? 1d : 0d, normResid, pOut, _martingale }
            });
        }

        if (_log.IsEnabled(LogLevel.Information))
        {
            int B = n - PreTrain;
            var qpair = PickQuantileIndices(Confidence);
            _log.LogInformation(
                "timesfm summary type={Type} monitor={Monitor} B={B} conf={Conf:0.##} band={Lo}%..{Hi}% outside={Outside} flagged={Flagged} near={Near} maxResid={Max:0.###} minMargin={Min:0.###} coolDown={Cooldown} maxM={MaxM:0.###}",
                _modelType, _monitorPingInfoID, B, Confidence, qpair.loIdx * 10, qpair.hiIdx * 10,
                outsideCnt, flaggedCnt, near, maxResid, double.IsInfinity(minMargin) ? double.NaN : minMargin, _cooldown, _maxMartingaleThisBatch
            );
            foreach (var line in samples)
                _log.LogInformation("timesfm sample type={Type} {Line}", _modelType, line);
        }

        for (int k = 0; k < goodIndices.Count; k++)
            allPreds[goodIndices[k]] = preds[k];

        return allPreds;
    }

    public void PrintPrediction(IEnumerable<AnomalyPrediction> predictions)
    {
        var sb = new StringBuilder();
        sb.Append($"[{_monitorPingInfoID}] TimesFM({_modelType}) preds: ");
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
        _log.LogDebug("[TimesFM] {ModelType} monitor {MonitorId} streaming request...", _modelType, _monitorPingInfoID);

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
        _log.LogDebug("[TimesFM] {ModelType} monitor {MonitorId} completed stream ({CharCount} chars)", _modelType, _monitorPingInfoID, sb.Length);
        return sb.ToString();
    }

    private TimesFmResponse ParseTimesFmResponse(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        TimesFmResponse? primary = null;
        List<JsonElement>? extras = null;

        while (JsonDocument.TryParseValue(ref reader, out var doc))
        {
            using var disposableDoc = doc;
            if (primary == null)
            {
                primary = disposableDoc.Deserialize<TimesFmResponse>();
                if (primary == null)
                    throw new InvalidOperationException("TimesFM: null response payload");
            }
            else
            {
                extras ??= new List<JsonElement>();
                extras.Add(disposableDoc.RootElement.Clone());
            }
        }

        if (extras is { Count: > 0 })
        {
            foreach (var extra in extras)
            {
                string info = extra.ValueKind switch
                {
                    JsonValueKind.Object => string.Join(",", extra.EnumerateObject().Select(p => p.Name)),
                    JsonValueKind.Array => $"array[{extra.GetArrayLength()}]",
                    JsonValueKind.String => "string",
                    JsonValueKind.Number => "number",
                    JsonValueKind.True or JsonValueKind.False => "bool",
                    JsonValueKind.Null => "null",
                    _ => extra.ValueKind.ToString()
                };
                _log.LogInformation("TimesFM supplemental payload for monitor {Monitor}: kind={Kind} head={Info}", _monitorPingInfoID, extra.ValueKind, info);
            }
            _log.LogInformation("TimesFM response for monitor {Monitor} included {Count} supplemental payload(s); storing extras for diagnostics", _monitorPingInfoID, extras.Count);
            // Future: wire extras into diagnostics if needed.
        }

        return primary ?? throw new InvalidOperationException("TimesFM: no primary payload found");
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

        if (r.Forecast is JsonElement rawEl)
        {
            throw new InvalidOperationException($"TimesFM: unknown forecast shape {rawEl.ValueKind}: {rawEl.GetRawText()}");
        }

        throw new InvalidOperationException($"TimesFM: unknown forecast shape type={r.Forecast?.GetType().Name ?? "null"}");
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
