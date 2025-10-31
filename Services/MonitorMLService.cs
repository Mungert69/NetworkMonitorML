using Microsoft.ML;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using NetworkMonitor.Utils.Helpers;
using System.Threading.Tasks;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Data;
using NetworkMonitor.ML.Data;
using NetworkMonitor.ML.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using NetworkMonitor.ML.Repository;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
namespace NetworkMonitor.ML.Services;
public interface IMonitorMLService
{
    Task Init();
    Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj);
    Task<List<LocalPingInfo>> TrainForHost(int monitorPingInfoID);
    DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos, int monitorIPID);
    DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos, int monitorIPID, int spikeDetectionThreshold);
    Task<DetectionResult> InitChangeDetection(MonitorPingInfo monitorPingInfo);
    Task<DetectionResult> InitSpikeDetection(MonitorPingInfo monitorPingInfo);
    Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> CheckHost(int monitorIPID, int dataSetID);
    Task<TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>>> CheckLatestHostsTest();
    Task<ResultObj> CheckLatestHosts();
    ResultObj UpdatePingInfos(ProcessorDataObj processorDataObj);
    Task<List<ResultObj>> UpdateAlertSent(List<int> monitorIPIDs, bool alertSent);
    Task<List<ResultObj>> UpdateAlertFlag(List<int> monitorIPIDs, bool alertSent);
    Task<List<ResultObj>> ResetAlerts(List<int> monitorIPIDs);
    int PredictWindow { get; set; }
    int MartingaleDetectionThreshold { get; set; }
    int SpikeDetectionThreshold { get; set; }
    double SpikeConfidence { get; set; }
    double ChangeConfidence { get; set; }
    public int ChangePreTrain { get; set; }
    public int SpikePreTrain { get; set; }
}
public class MonitorMLService : IMonitorMLService
{
    private enum DetectionBackend
    {
        Primary,
        Secondary
    }

    private readonly Dictionary<(int monitorIPID, string modelType), IMLModel> _models = new();
    private readonly Dictionary<(int monitorIPID, string modelType), IMLModel> _secondaryModels = new();
    private ILogger _logger;
    private IRabbitRepo _rabbitRepo;
    private int _martingaleDetectionThreshold = 100;
    //private IServiceScopeFactory _scopeFactory;
    private readonly IMLModelFactory _mlModelFactory;
    private readonly IMLModelFactory? _secondaryModelFactory;
    private readonly IMonitorMLDataRepo _monitorMLDataRepo;
    private SystemParams _systemParams;
    private MLParams _mlParams;
    private readonly ConcurrentDictionary<int, ResolvedHostParameters> _hostParameters = new();
    private readonly AdaptiveWindowManager _windowManager;
    private bool _isRunning;
    private readonly bool _isHybrid;
    private DeviationAnalyzer _deviationAnalyzer = new DeviationAnalyzer(10, 1);

    private sealed class ResolvedHostParameters
    {
        public ResolvedModelParameters Primary { get; init; } = new();
        public ResolvedModelParameters? Secondary { get; init; }
    }
    public int PredictWindow
    {
        get => _mlParams.PredictWindow;
        set
        {
            _mlParams.PredictWindow = value;
            ReconfigureAdaptiveSettings();
        }
    }
    public int MartingaleDetectionThreshold { get => _martingaleDetectionThreshold; set => _martingaleDetectionThreshold = value; }
    public int SpikeDetectionThreshold { get => _mlParams.SpikeDetectionThreshold; set => _mlParams.SpikeDetectionThreshold = value; }
    public double SpikeConfidence { get => _mlParams.SpikeConfidence; set => _mlParams.SpikeConfidence = value; }
    public double ChangeConfidence { get => _mlParams.ChangeConfidence; set => _mlParams.ChangeConfidence = value; }
    public int ChangePreTrain
    {
        get => _mlParams.ChangePreTrain;
        set
        {
            _mlParams.ChangePreTrain = value;
            ReconfigureAdaptiveSettings();
        }
    }
    public int SpikePreTrain
    {
        get => _mlParams.SpikePreTrain;
        set
        {
            _mlParams.SpikePreTrain = value;
            ReconfigureAdaptiveSettings();
        }
    }
    public MonitorMLService(ILogger<MonitorMLService> logger, IMonitorMLDataRepo monitorMLDataRepo, IMLModelFactory mlModelFactory, IRabbitRepo rabbitRepo, ISystemParamsHelper systemParamsHelper, ISecondaryModelFactory? secondaryModelFactory = null)
    {
        _logger = logger;
        //_scopeFactory = scopeFactory;
        _mlModelFactory = mlModelFactory;
        _secondaryModelFactory = secondaryModelFactory;
        _monitorMLDataRepo = monitorMLDataRepo;
        _rabbitRepo = rabbitRepo;
        _systemParams = systemParamsHelper.GetSystemParams();
        _mlParams = systemParamsHelper.GetMLParams();
        _isHybrid = !string.IsNullOrEmpty(_mlParams.SecondaryModelSelection);
        if (_isHybrid && _secondaryModelFactory == null)
        {
            throw new InvalidOperationException("Hybrid model selection requires a secondary model factory.");
        }
        _windowManager = new AdaptiveWindowManager(BuildAdaptiveSettings(), logger);
        ReconfigureAdaptiveSettings();
        Init().Wait();
    }

    private ResolvedHostParameters ResolveParameters(MonitorModelConfig? hostConfig)
    {
        var primaryResolved = _mlParams.ActiveModelParameters.Clone();
        ResolvedModelParameters? secondaryResolved = _isHybrid ? _mlParams.SecondaryModelParameters.Clone() : null;

        if (hostConfig != null)
        {
            void ApplyCommon(Action<ResolvedModelParameters> setter)
            {
                setter(primaryResolved);
                if (secondaryResolved != null)
                {
                    setter(secondaryResolved);
                }
            }

            if (hostConfig.ChangeConfidence.HasValue)
                ApplyCommon(r => r.ChangeConfidence = hostConfig.ChangeConfidence.Value);
            if (hostConfig.SpikeConfidence.HasValue)
                ApplyCommon(r => r.SpikeConfidence = hostConfig.SpikeConfidence.Value);
            if (hostConfig.ChangePreTrain.HasValue)
                ApplyCommon(r => r.ChangePreTrain = hostConfig.ChangePreTrain.Value);
            if (hostConfig.SpikePreTrain.HasValue)
                ApplyCommon(r => r.SpikePreTrain = hostConfig.SpikePreTrain.Value);
            if (hostConfig.PredictWindow.HasValue)
                ApplyCommon(r => r.PredictWindow = hostConfig.PredictWindow.Value);
            if (hostConfig.SpikeDetectionThreshold.HasValue)
                ApplyCommon(r => r.SpikeDetectionThreshold = hostConfig.SpikeDetectionThreshold.Value);

            void ApplySharedTimesFmSettings(TimesFmResolvedSettings target)
            {
                if (hostConfig.RunLength.HasValue)
                    target.RunLength = hostConfig.RunLength.Value;
                if (hostConfig.KOfNK.HasValue)
                    target.KOfNK = hostConfig.KOfNK.Value;
                if (hostConfig.KOfNN.HasValue)
                    target.KOfNN = hostConfig.KOfNN.Value;
                if (hostConfig.MadAlpha.HasValue)
                    target.MadAlpha = hostConfig.MadAlpha.Value;
                if (hostConfig.MinBandAbs.HasValue)
                    target.MinBandAbs = hostConfig.MinBandAbs.Value;
                if (hostConfig.MinBandRel.HasValue)
                    target.MinBandRel = hostConfig.MinBandRel.Value;
                if (hostConfig.RollSigmaWindow.HasValue)
                    target.RollSigmaWindow = hostConfig.RollSigmaWindow.Value;
                if (hostConfig.BaselineWindow.HasValue)
                    target.BaselineWindow = hostConfig.BaselineWindow.Value;
                if (hostConfig.SigmaCooldown.HasValue)
                    target.SigmaCooldown = hostConfig.SigmaCooldown.Value;
                if (hostConfig.MinRelShift.HasValue)
                    target.MinRelShift = hostConfig.MinRelShift.Value;
                if (hostConfig.SampleRows.HasValue)
                    target.SampleRows = hostConfig.SampleRows.Value;
                if (hostConfig.NearMissFraction.HasValue)
                    target.NearMissFraction = hostConfig.NearMissFraction.Value;
                if (hostConfig.LogJson.HasValue)
                    target.LogJson = hostConfig.LogJson.Value;
            }

            void ApplyModeSpecificTimesFmSettings(TimesFmResolvedSettings target, bool isChange)
            {
                if (isChange)
                {
                    if (hostConfig.ChangeRunLength.HasValue)
                        target.RunLength = hostConfig.ChangeRunLength.Value;
                    if (hostConfig.ChangeKOfNK.HasValue)
                        target.KOfNK = hostConfig.ChangeKOfNK.Value;
                    if (hostConfig.ChangeKOfNN.HasValue)
                        target.KOfNN = hostConfig.ChangeKOfNN.Value;
                    if (hostConfig.ChangeMadAlpha.HasValue)
                        target.MadAlpha = hostConfig.ChangeMadAlpha.Value;
                    if (hostConfig.ChangeMinBandAbs.HasValue)
                        target.MinBandAbs = hostConfig.ChangeMinBandAbs.Value;
                    if (hostConfig.ChangeMinBandRel.HasValue)
                        target.MinBandRel = hostConfig.ChangeMinBandRel.Value;
                    if (hostConfig.ChangeRollSigmaWindow.HasValue)
                        target.RollSigmaWindow = hostConfig.ChangeRollSigmaWindow.Value;
                    if (hostConfig.ChangeBaselineWindow.HasValue)
                        target.BaselineWindow = hostConfig.ChangeBaselineWindow.Value;
                    if (hostConfig.ChangeSigmaCooldown.HasValue)
                        target.SigmaCooldown = hostConfig.ChangeSigmaCooldown.Value;
                    if (hostConfig.ChangeMinRelShift.HasValue)
                        target.MinRelShift = hostConfig.ChangeMinRelShift.Value;
                    if (hostConfig.ChangeSampleRows.HasValue)
                        target.SampleRows = hostConfig.ChangeSampleRows.Value;
                    if (hostConfig.ChangeNearMissFraction.HasValue)
                        target.NearMissFraction = hostConfig.ChangeNearMissFraction.Value;
                    if (hostConfig.ChangeLogJson.HasValue)
                        target.LogJson = hostConfig.ChangeLogJson.Value;
                }
                else
                {
                    if (hostConfig.SpikeRunLength.HasValue)
                        target.RunLength = hostConfig.SpikeRunLength.Value;
                    if (hostConfig.SpikeKOfNK.HasValue)
                        target.KOfNK = hostConfig.SpikeKOfNK.Value;
                    if (hostConfig.SpikeKOfNN.HasValue)
                        target.KOfNN = hostConfig.SpikeKOfNN.Value;
                    if (hostConfig.SpikeMadAlpha.HasValue)
                        target.MadAlpha = hostConfig.SpikeMadAlpha.Value;
                    if (hostConfig.SpikeMinBandAbs.HasValue)
                        target.MinBandAbs = hostConfig.SpikeMinBandAbs.Value;
                    if (hostConfig.SpikeMinBandRel.HasValue)
                        target.MinBandRel = hostConfig.SpikeMinBandRel.Value;
                    if (hostConfig.SpikeRollSigmaWindow.HasValue)
                        target.RollSigmaWindow = hostConfig.SpikeRollSigmaWindow.Value;
                    if (hostConfig.SpikeBaselineWindow.HasValue)
                        target.BaselineWindow = hostConfig.SpikeBaselineWindow.Value;
                    if (hostConfig.SpikeSigmaCooldown.HasValue)
                        target.SigmaCooldown = hostConfig.SpikeSigmaCooldown.Value;
                    if (hostConfig.SpikeMinRelShift.HasValue)
                        target.MinRelShift = hostConfig.SpikeMinRelShift.Value;
                    if (hostConfig.SpikeSampleRows.HasValue)
                        target.SampleRows = hostConfig.SpikeSampleRows.Value;
                    if (hostConfig.SpikeNearMissFraction.HasValue)
                        target.NearMissFraction = hostConfig.SpikeNearMissFraction.Value;
                    if (hostConfig.SpikeLogJson.HasValue)
                        target.LogJson = hostConfig.SpikeLogJson.Value;
                }
            }

            ApplySharedTimesFmSettings(primaryResolved.TimesFmChangeSettings);
            ApplyModeSpecificTimesFmSettings(primaryResolved.TimesFmChangeSettings, isChange: true);
            ApplySharedTimesFmSettings(primaryResolved.TimesFmSpikeSettings);
            ApplyModeSpecificTimesFmSettings(primaryResolved.TimesFmSpikeSettings, isChange: false);

            if (secondaryResolved != null)
            {
                ApplySharedTimesFmSettings(secondaryResolved.TimesFmChangeSettings);
                ApplyModeSpecificTimesFmSettings(secondaryResolved.TimesFmChangeSettings, isChange: true);
                ApplySharedTimesFmSettings(secondaryResolved.TimesFmSpikeSettings);
                ApplyModeSpecificTimesFmSettings(secondaryResolved.TimesFmSpikeSettings, isChange: false);
            }
        }

        return new ResolvedHostParameters
        {
            Primary = primaryResolved,
            Secondary = secondaryResolved
        };
    }

    private void LogMlnetDiagnostics(string mode, int monitorIPID, List<AnomalyPrediction> predictions, List<LocalPingInfo> pingInfos, int preTrain)
    {
        if (!_logger.IsEnabled(LogLevel.Debug) || predictions.Count == 0)
            return;

        var alertCount = predictions.Count(p => p.Prediction.Length > 0 && p.Prediction[0] == 1);
        var maxScore = predictions.Max(p => p.Prediction.Length > 1 ? p.Prediction[1] : 0d);
        var minPValue = predictions.Min(p => p.Prediction.Length > 2 ? p.Prediction[2] : 1d);

        _logger.LogDebug("[ML.NET] {Mode} monitor={Monitor} total={Total} alerts={Alerts} maxScore={MaxScore:F3} minP={MinP:F3} preTrain={PreTrain}",
            mode, monitorIPID, predictions.Count, alertCount, maxScore, minPValue, preTrain);

        static bool IsSampleIndex(int idx, int total)
        {
            if (total <= 6) return true;
            return idx < 4 || idx >= total - 2;
        }

        foreach (var (prediction, index) in predictions.Select((pred, idx) => (pred, idx)).Where(t => IsSampleIndex(t.idx, predictions.Count)))
        {
            var timestamp = index < pingInfos.Count ? pingInfos[index].DateSentInt : 0u;
            double alert = prediction.Prediction.Length > 0 ? prediction.Prediction[0] : 0d;
            double score = prediction.Prediction.Length > 1 ? prediction.Prediction[1] : 0d;
            double p = prediction.Prediction.Length > 2 ? prediction.Prediction[2] : 0.5d;
            double martingale = prediction.Prediction.Length > 3 ? prediction.Prediction[3] : 1d;
            _logger.LogDebug("[ML.NET] {Mode} monitor={Monitor} sample#{Sample} ts={Timestamp} alert={Alert} score={Score:F3} p={P:F3} M={Martingale:F3}",
                mode, monitorIPID, index, timestamp, alert, score, p, martingale);
        }
    }

    private AdaptiveWindowManager.AdaptiveWindowSettings BuildAdaptiveSettings()
    {
        int changePreTrainMin = Math.Max(15, _mlParams.ChangePreTrain / 2);
        changePreTrainMin = Math.Min(changePreTrainMin, _mlParams.ChangePreTrain);
        int spikePreTrainMin = Math.Max(10, _mlParams.SpikePreTrain / 2);
        spikePreTrainMin = Math.Min(spikePreTrainMin, _mlParams.SpikePreTrain);

        int changeMax = Math.Max(_mlParams.PredictWindow, _mlParams.ChangePreTrain + 1);
        int spikeMax = Math.Max(_mlParams.PredictWindow, _mlParams.SpikePreTrain + 1);

        int changeMin = Math.Max(40, changePreTrainMin + 1);
        if (changeMin >= changeMax)
            changeMin = Math.Max(changePreTrainMin + 1, changeMax - 1);

        int spikeMin = Math.Max(20, spikePreTrainMin + 1);
        if (spikeMin >= spikeMax)
            spikeMin = Math.Max(spikePreTrainMin + 1, spikeMax - 1);

        int changeStep = Math.Max(5, (changeMax - changeMin) / 6);
        if (changeStep == 0) changeStep = 5;

        int spikeStep = Math.Max(5, (spikeMax - spikeMin) / 6);
        if (spikeStep == 0) spikeStep = 5;

        int changePreTrainStep = Math.Max(1, (_mlParams.ChangePreTrain - changePreTrainMin) / 2);
        if (changePreTrainStep < 5) changePreTrainStep = 5;
        int spikePreTrainStep = Math.Max(1, (_mlParams.SpikePreTrain - spikePreTrainMin) / 2);
        if (spikePreTrainStep < 5) spikePreTrainStep = 5;

        return new AdaptiveWindowManager.AdaptiveWindowSettings(
            MaxChangeWindow: changeMax,
            MaxSpikeWindow: spikeMax,
            MinChangeWindow: changeMin,
            MinSpikeWindow: spikeMin,
            ChangeStep: changeStep,
            SpikeStep: spikeStep,
            ChangePreTrainMax: _mlParams.ChangePreTrain,
            SpikePreTrainMax: _mlParams.SpikePreTrain,
            ChangePreTrainMin: changePreTrainMin,
            SpikePreTrainMin: spikePreTrainMin,
            ChangePreTrainStep: changePreTrainStep,
            SpikePreTrainStep: spikePreTrainStep,
            GrowMartingale: 1.05,
            ShrinkMartingale: 1.03,
            GrowBoostThreshold: 0.05,
            ShrinkBoostThreshold: 0.03,
            AlertCooldown: TimeSpan.FromMinutes(10));
    }

    private void ReconfigureAdaptiveSettings()
    {
        _windowManager?.ApplySettings(BuildAdaptiveSettings());
    }
    public async Task Init()
    {
        await PublishRepo.PredictReady(_logger, _rabbitRepo, false);
        try
        {
            await _monitorMLDataRepo.GetLatestMonitorPingInfos(_mlParams.PredictWindow);
        }
        catch (Exception e)
        {
            _logger.LogCritical($" Error : unable to init Service . Error was : {e.Message}");
        }
         await PublishRepo.PredictReady(_logger, _rabbitRepo, true);
    }
    private async Task EnsureModelInitialized(int monitorIPID, string modelType, double confidence, int preTrain)
    {
        var key = (monitorIPID, modelType);
        if (!_models.ContainsKey(key))
        {
            await GetOrCreateModel(monitorIPID, modelType, confidence, preTrain);
        }
        if (_models.TryGetValue(key, out var model) && model is TimesFmRabbitModel tfModel)
        {
            tfModel.PreTrain = preTrain;
            tfModel.Confidence = confidence;
            var resolved = ResolveParametersForBackend(monitorIPID, DetectionBackend.Primary);
            var settings = string.Equals(modelType, "Change", StringComparison.OrdinalIgnoreCase)
                ? resolved.TimesFmChangeSettings.Clone()
                : resolved.TimesFmSpikeSettings.Clone();
            tfModel.ApplySettings(settings);
        }
    }

    private async Task<IMLModel> GetOrCreateModel(int monitorIPID, string modelType, double confidence, int preTrain)
    {
        var key = (monitorIPID, modelType);
        if (!_models.TryGetValue(key, out var model))
        {
            model = _mlModelFactory.CreateModel(modelType, monitorIPID, confidence, preTrain);
            _models[key] = model;
        }
        return model;
    }

    private async Task EnsureSecondaryModelInitialized(int monitorIPID, string modelType, double confidence, int preTrain)
    {
        if (_secondaryModelFactory == null)
        {
            throw new InvalidOperationException("Secondary model factory not configured.");
        }
        var key = (monitorIPID, modelType);
        if (!_secondaryModels.ContainsKey(key))
        {
            await GetOrCreateSecondaryModel(monitorIPID, modelType, confidence, preTrain);
        }
        if (_secondaryModels.TryGetValue(key, out var model) && model is TimesFmRabbitModel tfModel)
        {
            tfModel.PreTrain = preTrain;
            tfModel.Confidence = confidence;
            var resolved = ResolveParametersForBackend(monitorIPID, DetectionBackend.Secondary);
            var settings = string.Equals(modelType, "Change", StringComparison.OrdinalIgnoreCase)
                ? resolved.TimesFmChangeSettings.Clone()
                : resolved.TimesFmSpikeSettings.Clone();
            tfModel.ApplySettings(settings);
        }
    }

    private async Task<IMLModel> GetOrCreateSecondaryModel(int monitorIPID, string modelType, double confidence, int preTrain)
    {
        var key = (monitorIPID, modelType);
        if (!_secondaryModels.TryGetValue(key, out var model))
        {
            model = _secondaryModelFactory!.CreateModel(modelType, monitorIPID, confidence, preTrain);
            _secondaryModels[key] = model;
        }
        return model;
    }

    private ResolvedModelParameters ResolveParametersForBackend(int monitorIPID, DetectionBackend backend)
    {
        if (_hostParameters.TryGetValue(monitorIPID, out var bundle))
        {
            return backend == DetectionBackend.Primary
                ? bundle.Primary
                : bundle.Secondary ?? _mlParams.SecondaryModelParameters;
        }

        return backend == DetectionBackend.Primary
            ? _mlParams.ActiveModelParameters
            : _mlParams.SecondaryModelParameters;
    }
    public async Task<ResultObj> ProcessAllHosts()
    {
        ResultObj result = new ResultObj();
        try
        {
            var monitorIdsAndDataSetIds = await _monitorMLDataRepo.GetMonitorIPIDDataSetIDs();
            foreach (var (monitorIPID, dataSetID) in monitorIdsAndDataSetIds)
            {
                var checkHostResult = await CheckHost(monitorIPID, dataSetID);
                // Log the detection results for each monitor
            }
            result.Success = true;
            result.Message = "Processed all monitors successfully.";
            // Optionally, set result.Data to some relevant data
        }
        catch (Exception e)
        {
            _logger.LogError($"Error processing all monitors: {e.Message}");
            result.Success = false;
            result.Message = $"Error processing all monitors: {e.Message}";
        }
        return result;
    }
    public async Task<ResultObj> CheckLatestHosts()
    {
        if (_isRunning)
        {
            _logger.LogWarning("CheckLatestHosts call ignored because a run is already in progress");
            return new ResultObj
            {
                Success = false,
                Message = "Predict service busy; skipping duplicate run"
            };
        }
         await PublishRepo.PredictReady(_logger, _rabbitRepo, false);
        TResultObj<List<TResultObj<(DetectionResult changeResult, DetectionResult SpikeResult)>>> testResult = await CheckLatestHostsTest();
        var result = new ResultObj();
        result.Success = testResult.Success;
        result.Message = testResult.Message;
         await PublishRepo.PredictReady(_logger, _rabbitRepo, true);
        return result;
    }
    public async Task<TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>>> CheckLatestHostsTest()
    {
        if (_isRunning)
        {
            _logger.LogWarning("CheckLatestHostsTest call ignored because a run is already in progress");
            return new TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>>()
            {
                Success = false,
                Message = "Predict service busy; skipping duplicate run",
                Data = new List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>()
            };
        }
        TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>> result = new TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>>();
        result.Message = " SERVICE : CheckLatestHosts : ";
        result.Success = true;
        try
        {
            // Assuming there's a method to get the latest MonitorPingInfos with a specified window size
            // This method needs to be implemented in the IMonitorMLDataRepo and MonitorMLDataRepo
            _isRunning = true;
            var latestMonitorPingInfos = await _monitorMLDataRepo.GetLatestMonitorPingInfos(_mlParams.PredictWindow);
            if (latestMonitorPingInfos == null || !latestMonitorPingInfos.Any())
            {
                result.Success = false;
                result.Message = "No latest MonitorPingInfo records found.";
                return result;
            }
            var results = new List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>();
            foreach (var monitorPingInfo in latestMonitorPingInfos.Where(w => w.Enabled))
            {
                if (monitorPingInfo.PingInfos.Count < _mlParams.PredictWindow)
                {
                    _logger.LogError($" Error : not enough PingInfos in last two data sets for MonitorPingInfo with ID {monitorPingInfo.ID} EndPointType {monitorPingInfo.EndPointType}");
                }
                else results.Add(await CheckHost(monitorPingInfo));
            }
            ResultObj resultPublish = new ResultObj();
            if (_systemParams.ServiceID != null && _systemParams.ServiceAuthKey != null)
            {
                resultPublish = await PublishRepo.MonitorPingInfos(_logger, _rabbitRepo, latestMonitorPingInfos, _systemParams.ServiceID, _systemParams.ServiceAuthKey);
            }
            else
            {
                resultPublish.Success = false;
                resultPublish.Message = " Error : missing system paramters SerivceID and or ServiceAuthKey.";
            }
            result.Success = resultPublish.Success && results.Any(r => r.Success);
            result.Message += resultPublish.Message;
            result.Data = results;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error in CheckLatestHosts: {ex.Message}";
            _logger.LogError(result.Message);
        }
        _isRunning = false;
        return result;
    }
    public async Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> CheckHost(MonitorPingInfo? monitorPingInfo)
    {
        var result = new TResultObj<(DetectionResult changeDetectionResult, DetectionResult spikeDetectionResult)>();
        if (monitorPingInfo != null)
        {
            result.Success = true;
            int monitorIPID = monitorPingInfo.MonitorIPID;
            int dataSetID = monitorPingInfo.DataSetID;
            var resolvedBundle = ResolveParameters(monitorPingInfo.ModelConfig);
            monitorPingInfo.EffectiveModelParameters = resolvedBundle.Primary;
            _hostParameters.AddOrUpdate(monitorIPID, resolvedBundle, (_, _) => resolvedBundle);

            var primaryChangeResult = await InitChangeDetectionCore(monitorPingInfo, resolvedBundle.Primary, DetectionBackend.Primary, applyLatch: true);
            var primarySpikeResult = await InitSpikeDetectionCore(monitorPingInfo, resolvedBundle.Primary, DetectionBackend.Primary, applyLatch: true);

            var finalChangeResult = primaryChangeResult;
            var finalSpikeResult = primarySpikeResult;
            DetectionResult? secondaryChangeResult = null;
            DetectionResult? secondarySpikeResult = null;

            bool primaryGate = primaryChangeResult.IsIssueDetected && primarySpikeResult.IsIssueDetected;
            bool finalAlertFlag;

            if (_isHybrid && primaryGate && primaryChangeResult.Result.Success && primarySpikeResult.Result.Success)
            {
                var secondaryParams = resolvedBundle.Secondary ?? _mlParams.SecondaryModelParameters.Clone();
                monitorPingInfo.EffectiveModelParameters = secondaryParams;
                secondaryChangeResult = await InitChangeDetectionCore(monitorPingInfo, secondaryParams, DetectionBackend.Secondary, applyLatch: false);
                secondarySpikeResult = await InitSpikeDetectionCore(monitorPingInfo, secondaryParams, DetectionBackend.Secondary, applyLatch: false);
                monitorPingInfo.EffectiveModelParameters = resolvedBundle.Primary;
                finalChangeResult = secondaryChangeResult;
                finalSpikeResult = secondarySpikeResult;
                finalAlertFlag = secondaryChangeResult.Result.Success && secondarySpikeResult.Result.Success &&
                                 (secondaryChangeResult.IsIssueDetected || secondarySpikeResult.IsIssueDetected);
            }
            else
            {
                finalAlertFlag = primaryGate && primaryChangeResult.Result.Success && primarySpikeResult.Result.Success;
            }

            var updateTimestamp = monitorPingInfo.DateEnded ?? DateTime.UtcNow;
            var windowConfig = _windowManager.Update(
                monitorIPID,
                new AdaptiveWindowManager.DetectionSnapshot(finalChangeResult.IsIssueDetected, finalChangeResult.NumberOfDetections, finalChangeResult.MaxMartingaleValue),
                new AdaptiveWindowManager.DetectionSnapshot(finalSpikeResult.IsIssueDetected, finalSpikeResult.NumberOfDetections, finalSpikeResult.MaxMartingaleValue),
                updateTimestamp);

            var combinedAnalysis = BuildHybridAnalysis(primaryChangeResult, primarySpikeResult, secondaryChangeResult, secondarySpikeResult, primaryGate, finalAlertFlag);
            result.Success = (primaryChangeResult.Result.Success && primarySpikeResult.Result.Success) &&
                             (!(_isHybrid && primaryGate) || (secondaryChangeResult?.Result.Success ?? true) && (secondarySpikeResult?.Result.Success ?? true));
            result.Message = combinedAnalysis;
            result.Data = (finalChangeResult, finalSpikeResult);
            _logger.LogDebug($"Combined analysis for MonitorIPID {monitorIPID}: {combinedAnalysis}");
            _logger.LogDebug("Adaptive windows after run monitor={Monitor} changeWindow={ChangeWindow} spikeWindow={SpikeWindow}", monitorIPID, windowConfig.ChangeWindow, windowConfig.SpikeWindow);
            var predictStatus = monitorPingInfo.PredictStatus;
            if (predictStatus == null)
            {
                try
                {
                    await _monitorMLDataRepo.SearchOrCreatePredictStatus(monitorPingInfo);
                }
                catch (Exception e)
                {
                    result.Success = false;
                    result.Message += $" Error : search or create  PredictStatus for MonitorPingInfo.MonitorIPID {monitorPingInfo.MonitorIPID} DataSetID {monitorPingInfo.DataSetID} . Error was : {e.Message}";
                }
            }
            predictStatus = monitorPingInfo.PredictStatus;
            if (predictStatus != null)
            {
                predictStatus.ChangeDetectionResult = finalChangeResult;
                predictStatus.SpikeDetectionResult = finalSpikeResult;
                predictStatus.EventTime = monitorPingInfo.DateEnded;
                predictStatus.AlertFlag = finalAlertFlag;
                if (predictStatus.AlertFlag)
                {
                    _logger.LogInformation($"MonitorPingInfo: {monitorPingInfo.ID} - {combinedAnalysis}");
                }
                else if (primaryChangeResult.IsIssueDetected || primarySpikeResult.IsIssueDetected)
                {
                    _logger.LogInformation($"MonitorPingInfo: {monitorPingInfo.ID} - Detection present but alert suppressed (requires spike and change). Details: {combinedAnalysis}");
                }
                predictStatus.Message = combinedAnalysis;
                monitorPingInfo.PredictStatus = predictStatus;
                try
                {
                    await _monitorMLDataRepo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, predictStatus);
                }
                catch (Exception e)
                {
                    result.Success = false;
                    result.Message += $" Error : could not update Prediction results in database for MonitorPingInfo.MonitorIPID {monitorPingInfo.MonitorIPID} DataSetID {monitorPingInfo.DataSetID} . Error was : {e.Message}";
                }

            }
            else
            {
                result.Success = false;
                result.Message += $" Error : Even after searching and creating still PredictStatus is null! for MonitorPingInfo.MonitorIPID {monitorPingInfo.MonitorIPID} DataSetID {monitorPingInfo.DataSetID} ";
            }
        }
        else
        {
            result.Success = false;
            result.Message = " monitorPingInfo is null";
        }
        return result;
    }
    public async Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> CheckHost(int monitorIPID, int dataSetID)
    {
        var windowConfig = _windowManager.GetConfig(monitorIPID);
        int baseWindow = Math.Max(PredictWindow, 1);
        int fetchWindow = Math.Max(baseWindow, Math.Max(windowConfig.ChangeWindow, windowConfig.SpikeWindow));
        fetchWindow = Math.Max(fetchWindow, Math.Max(windowConfig.ChangePreTrain + 1, windowConfig.SpikePreTrain + 1));
        var monitorPingInfo = await _monitorMLDataRepo.GetMonitorPingInfo(monitorIPID, fetchWindow, dataSetID);
        return await CheckHost(monitorPingInfo);
    }
    private string BuildHybridAnalysis(
        DetectionResult primaryChange,
        DetectionResult primarySpike,
        DetectionResult? secondaryChange,
        DetectionResult? secondarySpike,
        bool primaryGate,
        bool finalAlert)
    {
        var primarySummary = AnalyzeResults(primaryChange, primarySpike);
        if (!_isHybrid)
        {
            return primarySummary;
        }

        if (!primaryGate || secondaryChange == null || secondarySpike == null)
        {
            return $"Primary (MicrosoftMLTS): {primarySummary} Secondary (TimesFM) skipped (primary gate not met).";
        }

        var secondarySummary = AnalyzeResults(secondaryChange, secondarySpike);
        var verdict = finalAlert ? "TimesFM confirmed alert." : "TimesFM vetoed alert.";
        return $"Primary (MicrosoftMLTS): {primarySummary} Secondary (TimesFM): {secondarySummary} Outcome: {verdict}";
    }

    private string AnalyzeResults(DetectionResult changeDetectionResult, DetectionResult spikeDetectionResult)
    {
        // Combining results from both models
        bool isChangeDetected = changeDetectionResult.IsIssueDetected;
        bool isSpikeDetected = spikeDetectionResult.IsIssueDetected;
        // Update to consider both models for max Martingale value
        double maxMartingaleValue = Math.Max(changeDetectionResult.MaxMartingaleValue, spikeDetectionResult.MaxMartingaleValue);
        // Analysis logic to give feedback
        string analysisFeedback = "Analysis: ";
        if (!changeDetectionResult.Result.Success || !spikeDetectionResult.Result.Success)
        {
            if (!changeDetectionResult.Result.Success) analysisFeedback += $" Change Detection failed with Message: {changeDetectionResult.Result.Message}.";
            if (!spikeDetectionResult.Result.Success) analysisFeedback += $" Spike Detection failed with Message: {spikeDetectionResult.Result.Message}.";
            return analysisFeedback;
        }
        if (isChangeDetected && isSpikeDetected)
        {
            analysisFeedback += "Change and spike detections aligned; alert raised. ";
        }
        else if (isChangeDetected)
        {
            analysisFeedback += "Change detected but spike not confirmed; alert suppressed. ";
        }
        else if (isSpikeDetected)
        {
            analysisFeedback += "Spike detected but baseline change not confirmed; alert suppressed. ";
        }
        else
        {
            analysisFeedback += "No significant issues detected.";
        }

        if (isChangeDetected)
        {
            var datePingInfo = new PingInfo();
            datePingInfo.DateSentInt = (uint)changeDetectionResult.IndexOfFirstDetection;
            var gatingNote = isSpikeDetected ? string.Empty : " Alert pending spike confirmation.";
            analysisFeedback += $"Changes detected: first change at {datePingInfo.DateSent} ,number of changes {changeDetectionResult.NumberOfDetections}, Avg Response Time: {changeDetectionResult.AverageScore:F2}, Certainty score : {changeDetectionResult.MinPValue:F2} (closer to zero is more certain).{gatingNote} ";
        }
        if (isSpikeDetected)
        {
            var datePingInfo = new PingInfo();
            datePingInfo.DateSentInt = (uint)spikeDetectionResult.IndexOfFirstDetection;
            var gatingNote = isChangeDetected ? string.Empty : " Alert pending change confirmation.";
            analysisFeedback += $"Spikes detected: first spike at {datePingInfo.DateSent}, number of spikes {spikeDetectionResult.NumberOfDetections}, Avg Response Time: {spikeDetectionResult.AverageScore:F2}, Certainty score: {spikeDetectionResult.MinPValue:F2} (closer to zero is more certain).{gatingNote} ";
        }
        // Adding Martingale value analysis if relevant
        if (maxMartingaleValue > MartingaleDetectionThreshold)
        { // Use a defined threshold based on your requirements
            analysisFeedback += $"Martingale value is high: {maxMartingaleValue:F2}, indicating a sudden change.";
        }
        return analysisFeedback;
    }
    private bool CheckMonitorPingInfoOK(MonitorPingInfo? monitorPingInfo, int monitorIPID, DetectionResult detectionResult)
    {
        if (monitorPingInfo == null)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Host with ID {monitorIPID} returned null.";
            return false;
        }
        if (monitorPingInfo.PingInfos == null)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Host with ID {monitorIPID} contains no event data.";
            return false;
        }
        int requiredWindow = monitorPingInfo.EffectiveModelParameters?.PredictWindow ?? PredictWindow;
        if (requiredWindow <= 0)
        {
            requiredWindow = PredictWindow;
        }

        if (monitorPingInfo.PingInfos.Count < requiredWindow)
        {
            detectionResult.Result.Success = false;
            int remaining = Math.Max(0, requiredWindow - monitorPingInfo.PingInfos.Count);
            detectionResult.Result.Message = $" Error : MonitorPingInfo with ID {monitorIPID} not enough data for prediction was retrieved . {remaining} more events needs to make a prediction.";
            return false;
        }
        return true;
    }
    // New methods to handle the single input case in service logic
    public async Task<DetectionResult> InitChangeDetection(LocalPingInfo input, int monitorIPID)
    {
        var detectionResult = new DetectionResult();
        try
        {
            await EnsureModelInitialized(monitorIPID, "Change", _mlParams.ChangeConfidence, ChangePreTrain);
            detectionResult = PredictForHostChange(input, monitorIPID);
        }
        catch (Exception e)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Could not run InitChangeDetection for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
            return detectionResult;
        }
        return detectionResult;
    }
    public async Task<DetectionResult> InitSpikeDetection(LocalPingInfo input, int monitorIPID)
    {
        var detectionResult = new DetectionResult();
        try
        {
            await EnsureModelInitialized(monitorIPID, "Spike", _mlParams.SpikeConfidence, SpikePreTrain);
            detectionResult = PredictForHostSpike(input, monitorIPID);
        }
        catch (Exception e)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Could not run InitChangeDetection for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
            return detectionResult;
        }
        return detectionResult;
    }
    public async Task<DetectionResult> InitChangeDetection(MonitorPingInfo monitorPingInfo)
    {
        var resolvedParameters = monitorPingInfo.EffectiveModelParameters ?? _mlParams.ActiveModelParameters;
        return await InitChangeDetectionCore(monitorPingInfo, resolvedParameters, DetectionBackend.Primary, applyLatch: true);
    }

    private async Task<DetectionResult> InitChangeDetectionCore(MonitorPingInfo monitorPingInfo, ResolvedModelParameters resolvedParams, DetectionBackend backend, bool applyLatch)
    {
        int monitorIPID = monitorPingInfo.MonitorIPID;
        var detectionResult = new DetectionResult();
        try
        {
            if (applyLatch && TryReuseLatchedDetection(monitorPingInfo, isChange: true, out var latched))
                return latched;
            if (!CheckMonitorPingInfoOK(monitorPingInfo, monitorIPID, detectionResult))
            {
                return detectionResult;
            }
            var localPingInfos = GetLocalPingInfos(monitorPingInfo!);
            var config = _windowManager.GetConfig(monitorIPID);
            var changeConfidence = resolvedParams.ChangeConfidence;
            var changePreTrain = resolvedParams.ChangePreTrain;
            var changeWindow = resolvedParams.PredictWindow > 0 ? resolvedParams.PredictWindow : config.ChangeWindow;
            changeWindow = Math.Max(1, changeWindow);
            var changeMaxWindow = Math.Max(config.MaxChangeWindow, changeWindow);
            changePreTrain = Math.Clamp(changePreTrain, 1, Math.Max(1, changeWindow - 1));

            _logger.LogDebug("Window config before {Backend} change detection monitor={Monitor} changeWindow={ChangeWindow} changePreTrain={ChangePreTrain} spikeWindow={SpikeWindow} spikePreTrain={SpikePreTrain}", backend, monitorIPID, changeWindow, changePreTrain, config.SpikeWindow, config.SpikePreTrain);

            localPingInfos = TrimWindow(localPingInfos, changeWindow, changeMaxWindow, changePreTrain);
            if (!HasSufficientUsableData("change", monitorIPID, localPingInfos, changeWindow, changePreTrain, detectionResult))
            {
                detectionResult.IsDataLimited = true;
                return detectionResult;
            }

            if (backend == DetectionBackend.Primary)
            {
                await EnsureModelInitialized(monitorIPID, "Change", changeConfidence, changePreTrain);
            }
            else
            {
                await EnsureSecondaryModelInitialized(monitorIPID, "Change", changeConfidence, changePreTrain);
            }

            detectionResult = PredictForHostChange(localPingInfos, monitorIPID, backend);
            _logger.LogDebug("Change detection ({Backend}) for MonitorPingInfoID {MonitorPingInfoId}", backend, monitorPingInfo.ID);
        }
        catch (Exception e)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Could not run {(backend == DetectionBackend.Primary ? "InitChangeDetection" : "TimesFM verification change detection")} for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
            return detectionResult;
        }
        return detectionResult;
    }
    public async Task<DetectionResult> InitSpikeDetection(MonitorPingInfo monitorPingInfo)
    {
        var resolvedParameters = monitorPingInfo.EffectiveModelParameters ?? _mlParams.ActiveModelParameters;
        return await InitSpikeDetectionCore(monitorPingInfo, resolvedParameters, DetectionBackend.Primary, applyLatch: true);
    }

    private async Task<DetectionResult> InitSpikeDetectionCore(MonitorPingInfo monitorPingInfo, ResolvedModelParameters resolvedParams, DetectionBackend backend, bool applyLatch)
    {
        int monitorIPID = monitorPingInfo.MonitorIPID;
        var detectionResult = new DetectionResult();
        try
        {
            if (applyLatch && TryReuseLatchedDetection(monitorPingInfo, isChange: false, out var latched))
                return latched;
            if (!CheckMonitorPingInfoOK(monitorPingInfo, monitorIPID, detectionResult))
            {
                return detectionResult;
            }
            var originalLocalPingInfos = GetLocalPingInfos(monitorPingInfo!);
            var config = _windowManager.GetConfig(monitorIPID);
            var spikeConfidence = resolvedParams.SpikeConfidence;
            var spikePreTrain = resolvedParams.SpikePreTrain;
            var spikeWindow = resolvedParams.PredictWindow > 0 ? resolvedParams.PredictWindow : config.SpikeWindow;
            spikeWindow = Math.Max(1, spikeWindow);
            var spikeMaxWindow = Math.Max(config.MaxSpikeWindow, spikeWindow);
            var spikeThreshold = resolvedParams.SpikeDetectionThreshold > 0 ? resolvedParams.SpikeDetectionThreshold : SpikeDetectionThreshold;
            spikePreTrain = Math.Clamp(spikePreTrain, 1, Math.Max(1, spikeWindow - 1));

            _logger.LogDebug("Window config before {Backend} spike detection monitor={Monitor} changeWindow={ChangeWindow} changePreTrain={ChangePreTrain} spikeWindow={SpikeWindow} spikePreTrain={SpikePreTrain}", backend, monitorIPID, config.ChangeWindow, config.ChangePreTrain, spikeWindow, spikePreTrain);

            var trimmedLocalPingInfos = TrimWindow(originalLocalPingInfos, spikeWindow, spikeMaxWindow, spikePreTrain);
            if (!HasSufficientUsableData("spike", monitorIPID, trimmedLocalPingInfos, spikeWindow, spikePreTrain, detectionResult))
            {
                detectionResult.IsDataLimited = true;
                return detectionResult;
            }

            if (backend == DetectionBackend.Primary)
            {
                await EnsureModelInitialized(monitorIPID, "Spike", spikeConfidence, spikePreTrain);
            }
            else
            {
                await EnsureSecondaryModelInitialized(monitorIPID, "Spike", spikeConfidence, spikePreTrain);
            }

            detectionResult = PredictForHostSpike(trimmedLocalPingInfos, monitorIPID, spikeThreshold, backend);

            bool trimmedApplied = trimmedLocalPingInfos.Count != originalLocalPingInfos.Count;
            if (!detectionResult.IsIssueDetected
                && detectionResult.NumberOfDetections > 0
                && trimmedApplied
                && config.SpikeWindow < config.MaxSpikeWindow)
            {
                if (backend == DetectionBackend.Primary)
                {
                    await EnsureModelInitialized(monitorIPID, "Spike", spikeConfidence, spikePreTrain);
                }
                else
                {
                    await EnsureSecondaryModelInitialized(monitorIPID, "Spike", spikeConfidence, spikePreTrain);
                }
                detectionResult = PredictForHostSpike(originalLocalPingInfos, monitorIPID, spikeThreshold, backend);
            }
            _logger.LogDebug("Spike detection ({Backend}) for MonitorPingInfoID {MonitorPingInfoId}", backend, monitorPingInfo.ID);
        }
        catch (Exception e)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Could not run {(backend == DetectionBackend.Primary ? "InitSpikeDetection" : "TimesFM verification spike detection")} for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
            return detectionResult;
        }
        return detectionResult;
    }
    private List<LocalPingInfo> GetLocalPingInfos(MonitorPingInfo monitorPingInfo)
    {
        return monitorPingInfo.PingInfos.Select(pi => new LocalPingInfo
        {
            DateSentInt = pi.DateSentInt,
            RoundTripTime = (ushort)(pi.RoundTripTime ?? 0),
            StatusID = pi.StatusID
        }).ToList();
    }
    private static List<LocalPingInfo> TrimWindow(List<LocalPingInfo> source, int windowSize, int maxWindow, int preTrain)
    {
        if (windowSize >= maxWindow || source.Count <= windowSize)
            return source;
        int desired = Math.Max(windowSize, Math.Min(source.Count, windowSize + Math.Max(preTrain, windowSize / 2)));
        desired = Math.Min(source.Count, desired);
        int start = source.Count - desired;
        return source.GetRange(start, desired);
    }

    private bool HasSufficientUsableData(string mode, int monitorIPID, List<LocalPingInfo> data, int targetWindow, int preTrain, DetectionResult detectionResult)
    {
        int total = data.Count;
        int usable = data.Count(pi => !pi.IsTimeout());
        int required = Math.Max(targetWindow, preTrain + 1);
        if (usable >= required)
            return true;

        detectionResult.Result.Success = false;
        detectionResult.Result.Message = $" Warning : Skipped {mode} detection for monitor {monitorIPID} . Only {usable} usable points (total={total}) available but require at least {required} (window={targetWindow}, preTrain={preTrain}).";
        _logger.LogWarning("Skipping {Mode} detection for monitor {Monitor}: usable points {Usable} of {Total} < required {Required} (window={Window}, preTrain={PreTrain})", mode, monitorIPID, usable, total, required, targetWindow, preTrain);
        return false;
    }

    private bool TryReuseLatchedDetection(MonitorPingInfo monitorPingInfo, bool isChange, out DetectionResult detectionResult)
    {
        var status = monitorPingInfo.PredictStatus;
        if (status?.AlertSent == true)
        {
            var cached = isChange ? status.ChangeDetectionResult : status.SpikeDetectionResult;
            detectionResult = cached != null ? CloneDetectionResult(cached) : new DetectionResult
            {
                IsIssueDetected = true,
                NumberOfDetections = 1,
                Result = { Success = true }
            };
            detectionResult.Result.Success = true;
            detectionResult.Result.Message = "Skipped run: alert already sent";
            _logger.LogInformation("Skipping {Mode} detection for monitor {Monitor}: PredictStatus.AlertSent is true, reusing latched TimesFM results", isChange ? "change" : "spike", monitorPingInfo.MonitorIPID);
            return true;
        }

        detectionResult = null!;
        return false;
    }

    private static DetectionResult CloneDetectionResult(DetectionResult source)
    {
        return new DetectionResult
        {
            IsIssueDetected = source.IsIssueDetected,
            NumberOfDetections = source.NumberOfDetections,
            IsDataLimited = source.IsDataLimited,
            AverageScore = source.AverageScore,
            MinPValue = source.MinPValue,
            MaxMartingaleValue = source.MaxMartingaleValue,
            IndexOfFirstDetection = source.IndexOfFirstDetection,
            Result = new ResultObj
            {
                Success = source.Result.Success,
                Message = source.Result.Message
            }
        };
    }
    public async Task<List<LocalPingInfo>> TrainForHost(int monitorIPID)
    {
        var localPingInfos = new List<LocalPingInfo>();
        //var localPingInfos = await _monitorMLDataRepo.GetLocalPingInfosForHost(monitorIPID);
        /*if (localPingInfos.Count > 0)
        {
            //_mlModel.Train(localPingInfos);
            _logger.LogDebug($"MLSERVICE : Training PingInfo Data for host {monitorIPID}.");
        }*/
        return localPingInfos;
    }
    private Dictionary<(int monitorIPID, string modelType), IMLModel> GetModelDictionary(DetectionBackend backend)
        => backend == DetectionBackend.Primary ? _models : _secondaryModels;

    private DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos, int monitorIPID, DetectionBackend backend)
    {
        var result = new DetectionResult();
        var modelType = "Change";
        var models = GetModelDictionary(backend);
        var key = (monitorIPID, modelType);
        if (!models.TryGetValue(key, out var model))
        {
            throw new InvalidOperationException($"Model for MonitorIPID {monitorIPID} and ModelType {modelType} not found for backend {backend}.");
        }
        var predictions = model.PredictList(localPingInfos).ToList();
        result.IsIssueDetected = predictions.Any(p => p.Prediction[0] == 1);
        result.NumberOfDetections = predictions.Count(p => p.Prediction[0] == 1);
        string dateOfDetection = "N/A";
        // Check if there are any detections before calculating average and minimum
        if (result.NumberOfDetections > 0)
        {
            result.AverageScore = predictions.Where(p => p.Prediction[0] == 1).Average(p => p.Prediction[1]);
            result.MinPValue = predictions.Where(p => p.Prediction[0] == 1).Min(p => p.Prediction[2]);
            int index = predictions.FindIndex(p => p.Prediction[0] == 1);
            result.IndexOfFirstDetection = (int)localPingInfos[index].DateSentInt;
            var datePingInfo = new PingInfo() { DateSentInt = localPingInfos[index].DateSentInt };
            dateOfDetection = datePingInfo.DateSent.ToLongDateString() + " UTC";
        }
        // Ensure there are predictions before attempting to find the max Martingale value
        if (predictions.Any() && predictions[0].Prediction.Length > 3)
        {
            result.MaxMartingaleValue = predictions.Max(p => p.Prediction[3]);
        }
        var changeParams = ResolveParametersForBackend(monitorIPID, backend);
        LogMlnetDiagnostics("change", monitorIPID, predictions, localPingInfos, changeParams.ChangePreTrain);
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? $"An issue was detected at {dateOfDetection}" : "No issues detected")} with {result.NumberOfDetections} number of detections.";
        result.Result.Success = true;
        return result;
    }

    public DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos, int monitorIPID)
        => PredictForHostChange(localPingInfos, monitorIPID, DetectionBackend.Primary);
    private DetectionResult PredictForHostChange(LocalPingInfo input, int monitorIPID, DetectionBackend backend)
    {
        var result = new DetectionResult();
        var modelType = "Change";
        var models = GetModelDictionary(backend);
        var key = (monitorIPID, modelType);
        if (!models.TryGetValue(key, out var model))
        {
            throw new InvalidOperationException($"Model for MonitorIPID {monitorIPID} and ModelType {modelType} not found for backend {backend}.");
        }
        var prediction = model.Predict(input);
        result.IsIssueDetected = prediction.Prediction[0] == 1;
        result.NumberOfDetections = result.IsIssueDetected ? 1 : 0;
        result.AverageScore = prediction.Prediction[1];
        result.MinPValue = prediction.Prediction[2];
        // Martingale value
        if (prediction.Prediction.Length > 3)
        {
            result.MaxMartingaleValue = prediction.Prediction[3];
        }
        // Index of detection:
        result.IndexOfFirstDetection = result.IsIssueDetected ? 0 : -1;
        // 0 because it's the only input, -1 to signal no detection
        // Message
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? "An issue was detected." : "No issues detected.")}";
        result.Result.Success = true;
        return result;
    }
    private DetectionResult PredictForHostSpike(LocalPingInfo input, int monitorIPID, DetectionBackend backend)
    {
        var result = new DetectionResult();
        var modelType = "Spike";
        var models = GetModelDictionary(backend);
        var key = (monitorIPID, modelType);
        if (!models.TryGetValue(key, out var model))
        {
            throw new InvalidOperationException($"Model for MonitorIPID {monitorIPID} and ModelType {modelType} not found for backend {backend}.");
        }
        var prediction = model.Predict(input);
        result.IsIssueDetected = prediction.Prediction[0] == 1;
        result.NumberOfDetections = result.IsIssueDetected ? 1 : 0;
        result.AverageScore = prediction.Prediction[1];
        result.MinPValue = prediction.Prediction[2];
        if (prediction.Prediction.Length > 3)
        {
            result.MaxMartingaleValue = prediction.Prediction[3];
        }
        // Index of detection:
        result.IndexOfFirstDetection = result.IsIssueDetected ? 0 : -1;
        // 0 because it's the only input, -1 to signal no detection
        // Message
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? "An issue was detected." : "No issues detected.")}";
        result.Result.Success = true;
        return result;
    }

    public DetectionResult PredictForHostSpike(LocalPingInfo input, int monitorIPID)
        => PredictForHostSpike(input, monitorIPID, DetectionBackend.Primary);

    private DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos, int monitorIPID, int spikeDetectionThreshold, DetectionBackend backend)
    {
        var result = new DetectionResult();
        var modelType = "Spike";
        var models = GetModelDictionary(backend);
        var key = (monitorIPID, modelType);
        if (!models.TryGetValue(key, out var model))
        {
            throw new InvalidOperationException($"Model for MonitorIPID {monitorIPID} and ModelType {modelType} not found for backend {backend}.");
        }
        var predictions = model.PredictList(localPingInfos).ToList();
        result.NumberOfDetections = predictions.Count(p => p.Prediction[0] == 1);
        result.IsIssueDetected = result.NumberOfDetections >= spikeDetectionThreshold;
        string dateOfDetection = "N/A";
        // Check if there are any detections before calculating average and minimum
        if (result.NumberOfDetections > 0)
        {
            int index = predictions.FindIndex(p => p.Prediction[0] == 1);
            result.IndexOfFirstDetection = (int)localPingInfos[index].DateSentInt;
            var datePingInfo = new PingInfo() { DateSentInt = localPingInfos[index].DateSentInt };
            dateOfDetection = datePingInfo.DateSent.ToLongDateString() + " UTC";
            result.AverageScore = predictions.Where(p => p.Prediction[0] == 1).Average(p => p.Prediction[1]);
            result.MinPValue = predictions.Where(p => p.Prediction[0] == 1).Min(p => p.Prediction[2]);
        }
        if (predictions.Any() && predictions[0].Prediction.Length > 3)
        {
            result.MaxMartingaleValue = predictions.Max(p => p.Prediction[3]);
        }
        var resolvedParams = ResolveParametersForBackend(monitorIPID, backend);
        LogMlnetDiagnostics("spike", monitorIPID, predictions, localPingInfos, resolvedParams.SpikePreTrain);
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? $"An issue was detected at {dateOfDetection}" : "No issues detected")} with {result.NumberOfDetections} number of detections.";
        result.Result.Success = true;
        return result;
    }

    public DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos, int monitorIPID, int spikeDetectionThreshold)
        => PredictForHostSpike(localPingInfos, monitorIPID, spikeDetectionThreshold, DetectionBackend.Primary);

    public DetectionResult PredictForHostChange(LocalPingInfo input, int monitorIPID)
        => PredictForHostChange(input, monitorIPID, DetectionBackend.Primary);
    public async Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "Service : MLCheck : ";
        try
        {
            _logger.LogInformation(result.Message);
        }
        catch (Exception e)
        {
            result.Data = null;
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(result.Message);
        }
        return result;
    }
    public ResultObj UpdatePingInfos(ProcessorDataObj processorDataObj)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "Service : UpdatePingInfos : For Processor AuthID " + processorDataObj.AppID;
        if (_isRunning)
        {
            //TODO queue the Update until _isRunning=false
        }
        try
        {
            if (processorDataObj.MonitorPingInfos != null)
            {
                foreach (var monitorPingInfo in processorDataObj.MonitorPingInfos)
                {
                    monitorPingInfo.PingInfos = processorDataObj.PingInfos.Where(w => w.MonitorPingInfoID == monitorPingInfo.ID).ToList();
                    monitorPingInfo.DataSetID = 0;
                    var updateResult = _monitorMLDataRepo.UpdateMonitorPingInfo(monitorPingInfo);
                    if (!updateResult.Success)
                    {
                        result.Message += updateResult.Message;
                        return result;
                    }
                }
                result.Message += $" Success : updated {processorDataObj.MonitorPingInfos.Count} MonitorPingInfos , {processorDataObj.PingInfos.Count} PingInfos.";
            }
            if (processorDataObj.RemoveMonitorPingInfoIDs != null && processorDataObj.RemoveMonitorPingInfoIDs.Count != 0)
            {
                bool resultFlag = _monitorMLDataRepo.RemoveMonitorPingInfos(processorDataObj.RemoveMonitorPingInfoIDs);
                if (resultFlag) result.Message += $" Success : removed {processorDataObj.RemoveMonitorPingInfoIDs.Count} MonitorPingInfos .";
                else
                {
                    result.Success = false;
                    result.Message += " Error : could not remove MonitorPingInfos Data is not ready wait for 5 mins then try again .";
                }
            }
            result.Success = true;
            // _logger.LogInformation(result.Message);
        }
        catch (Exception e)
        {
            result.Data = null;
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(result.Message);
        }
        return result;
    }
    public async Task<List<ResultObj>> UpdateAlertSent(List<int> monitorIPIDs, bool alertSent)
    {
        var results = new List<ResultObj>();
        foreach (int id in monitorIPIDs)
        {
            var result = new ResultObj();
            result = await _monitorMLDataRepo.UpdatePredictStatusFlags(id, null, alertSent);
            results.Add(result);
        }
        return results;
    }
    // This method updates the AlertFlag field for multiple MonitorPingInfo objects based on the provided monitorIPIDs. The method returns a list of ResultObj objects indicating the success or failure of the update for each MonitorPingInfo. If the MonitorPingInfo with a given id is found in the MonitorPingInfos collection, the AlertFlag field is updated to the provided alertFlag value, and a success message is added to the ResultObj. If the MonitorPingInfo is not found, a failure message is added to the ResultObj.
    public async Task<List<ResultObj>> UpdateAlertFlag(List<int> monitorIPIDs, bool alertFlag)
    {
        var results = new List<ResultObj>();
        foreach (int id in monitorIPIDs)
        {
            var result = new ResultObj();
            result = await _monitorMLDataRepo.UpdatePredictStatusFlags(id, alertFlag, null);
            results.Add(result);
        }
        return results;
    }
    // This method resets the alert status for a list of MonitorPingInfos, specified by their monitorIPIDs, by setting the AlertFlag to false and AlertSent to false, and setting the DownCount to 0. It also publishes a message "alertMessageResetAlerts" with the list of AlertFlagObjs to the rabbitmq. The method returns a list of ResultObjs, which contains the success or failure of the operation and the relevant message.
    public async Task<List<ResultObj>> ResetAlerts(List<int> monitorIPIDs)
    {
        var results = new List<ResultObj>();
        ResultObj result;
        var alertFlagObjs = new List<AlertFlagObj>();
        foreach (int id in monitorIPIDs)
        {
            result = await _monitorMLDataRepo.UpdatePredictStatusFlags(id, false, false);
            alertFlagObjs.Add(new AlertFlagObj() { ID = id });
            results.Add(result);
        }
        results.Add(await PublishRepo.AlertMessgeResetPredictAlerts(_rabbitRepo, alertFlagObjs, _systemParams.ServiceID, _systemParams.ServiceAuthKey));
        return results;
    }
}
