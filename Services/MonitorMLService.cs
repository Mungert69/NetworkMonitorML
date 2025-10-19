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
    private Dictionary<(int monitorIPID, string modelType), IMLModel> _models = new Dictionary<(int monitorIPID, string modelType), IMLModel>();
    private ILogger _logger;
    private IRabbitRepo _rabbitRepo;
    private int _martingaleDetectionThreshold = 100;
    //private IServiceScopeFactory _scopeFactory;
    private readonly IMLModelFactory _mlModelFactory;
    private readonly IMonitorMLDataRepo _monitorMLDataRepo;
    private SystemParams _systemParams;
    private MLParams _mlParams;
    private readonly ConcurrentDictionary<int, ResolvedModelParameters> _hostParameters = new();
    private readonly AdaptiveWindowManager _windowManager;
    private bool _isRunning;
    private DeviationAnalyzer _deviationAnalyzer = new DeviationAnalyzer(10, 1);
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
    public MonitorMLService(ILogger<MonitorMLService> logger, IMonitorMLDataRepo monitorMLDataRepo, IMLModelFactory mlModelFactory, IRabbitRepo rabbitRepo, ISystemParamsHelper systemParamsHelper)
    {
        _logger = logger;
        //_scopeFactory = scopeFactory;
        _mlModelFactory = mlModelFactory;
        _monitorMLDataRepo = monitorMLDataRepo;
        _rabbitRepo = rabbitRepo;
        _systemParams = systemParamsHelper.GetSystemParams();
        _mlParams = systemParamsHelper.GetMLParams();
        _windowManager = new AdaptiveWindowManager(BuildAdaptiveSettings(), logger);
        ReconfigureAdaptiveSettings();
        Init().Wait();
    }

    private ResolvedModelParameters ResolveParameters(MonitorModelConfig? hostConfig)
    {
        var baselineTimesFm = _mlParams.ActiveModelParameters.TimesFmSettings;
        var resolved = new ResolvedModelParameters
        {
            ChangeConfidence = _mlParams.ChangeConfidence,
            SpikeConfidence = _mlParams.SpikeConfidence,
            ChangePreTrain = _mlParams.ChangePreTrain,
            SpikePreTrain = _mlParams.SpikePreTrain,
            PredictWindow = _mlParams.PredictWindow,
            SpikeDetectionThreshold = _mlParams.SpikeDetectionThreshold,
            TimesFmSettings = new TimesFmResolvedSettings
            {
                RunLength = baselineTimesFm.RunLength,
                KOfNK = baselineTimesFm.KOfNK,
                KOfNN = baselineTimesFm.KOfNN,
                MadAlpha = baselineTimesFm.MadAlpha,
                MinBandAbs = baselineTimesFm.MinBandAbs,
                MinBandRel = baselineTimesFm.MinBandRel,
                RollSigmaWindow = baselineTimesFm.RollSigmaWindow,
                BaselineWindow = baselineTimesFm.BaselineWindow,
                SigmaCooldown = baselineTimesFm.SigmaCooldown,
                MinRelShift = baselineTimesFm.MinRelShift,
                SampleRows = baselineTimesFm.SampleRows,
                NearMissFraction = baselineTimesFm.NearMissFraction,
                LogJson = baselineTimesFm.LogJson
            }
        };

        if (hostConfig == null)
            return resolved;

        if (hostConfig.ChangeConfidence.HasValue)
            resolved.ChangeConfidence = hostConfig.ChangeConfidence.Value;
        if (hostConfig.SpikeConfidence.HasValue)
            resolved.SpikeConfidence = hostConfig.SpikeConfidence.Value;
        if (hostConfig.ChangePreTrain.HasValue)
            resolved.ChangePreTrain = hostConfig.ChangePreTrain.Value;
        if (hostConfig.SpikePreTrain.HasValue)
            resolved.SpikePreTrain = hostConfig.SpikePreTrain.Value;
        if (hostConfig.PredictWindow.HasValue)
            resolved.PredictWindow = hostConfig.PredictWindow.Value;
        if (hostConfig.SpikeDetectionThreshold.HasValue)
            resolved.SpikeDetectionThreshold = hostConfig.SpikeDetectionThreshold.Value;

        var ts = resolved.TimesFmSettings;
        if (hostConfig.RunLength.HasValue)
            ts.RunLength = hostConfig.RunLength.Value;
        if (hostConfig.KOfNK.HasValue)
            ts.KOfNK = hostConfig.KOfNK.Value;
        if (hostConfig.KOfNN.HasValue)
            ts.KOfNN = hostConfig.KOfNN.Value;
        if (hostConfig.MadAlpha.HasValue)
            ts.MadAlpha = hostConfig.MadAlpha.Value;
        if (hostConfig.MinBandAbs.HasValue)
            ts.MinBandAbs = hostConfig.MinBandAbs.Value;
        if (hostConfig.MinBandRel.HasValue)
            ts.MinBandRel = hostConfig.MinBandRel.Value;
        if (hostConfig.RollSigmaWindow.HasValue)
            ts.RollSigmaWindow = hostConfig.RollSigmaWindow.Value;
        if (hostConfig.BaselineWindow.HasValue)
            ts.BaselineWindow = hostConfig.BaselineWindow.Value;
        if (hostConfig.SigmaCooldown.HasValue)
            ts.SigmaCooldown = hostConfig.SigmaCooldown.Value;
        if (hostConfig.MinRelShift.HasValue)
            ts.MinRelShift = hostConfig.MinRelShift.Value;
        if (hostConfig.SampleRows.HasValue)
            ts.SampleRows = hostConfig.SampleRows.Value;
        if (hostConfig.NearMissFraction.HasValue)
            ts.NearMissFraction = hostConfig.NearMissFraction.Value;
        if (hostConfig.LogJson.HasValue)
            ts.LogJson = hostConfig.LogJson.Value;

        resolved.TimesFmSettings = ts;
        return resolved;
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
            var settings = _hostParameters.TryGetValue(monitorIPID, out var resolved)
                ? resolved.TimesFmSettings
                : _mlParams.ActiveModelParameters.TimesFmSettings;
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
            var resolvedParams = ResolveParameters(monitorPingInfo.ModelConfig);
            monitorPingInfo.EffectiveModelParameters = resolvedParams;
            _hostParameters.AddOrUpdate(monitorIPID, resolvedParams, (_, _) => resolvedParams);
            var changeDetectionResult = await InitChangeDetection(monitorPingInfo);
            var spikeDetectionResult = await InitSpikeDetection(monitorPingInfo);
            var updateTimestamp = monitorPingInfo.DateEnded ?? DateTime.UtcNow;
            var windowConfig = _windowManager.Update(
                monitorIPID,
                new AdaptiveWindowManager.DetectionSnapshot(changeDetectionResult.IsIssueDetected, changeDetectionResult.NumberOfDetections, changeDetectionResult.MaxMartingaleValue),
                new AdaptiveWindowManager.DetectionSnapshot(spikeDetectionResult.IsIssueDetected, spikeDetectionResult.NumberOfDetections, spikeDetectionResult.MaxMartingaleValue),
                updateTimestamp);
            var combinedAnalysis = AnalyzeResults(changeDetectionResult, spikeDetectionResult);
            result.Success = changeDetectionResult.Result.Success && spikeDetectionResult.Result.Success;
            result.Message = combinedAnalysis;
            result.Data = (changeDetectionResult, spikeDetectionResult);
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
                predictStatus.ChangeDetectionResult = changeDetectionResult;
                predictStatus.SpikeDetectionResult = spikeDetectionResult;
                predictStatus.EventTime = monitorPingInfo.DateEnded;
                if (changeDetectionResult.IsIssueDetected || spikeDetectionResult.IsIssueDetected)
                {
                    //predictStatus.AlertFlag = true;
                    _logger.LogInformation($"MonitorPingInfo: {monitorPingInfo.ID} - {combinedAnalysis}");
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
        var monitorPingInfo = await _monitorMLDataRepo.GetMonitorPingInfo(monitorIPID, PredictWindow, dataSetID);
        return await CheckHost(monitorPingInfo);
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
            analysisFeedback += "Possible issues detected. ";
            if (isChangeDetected)
            {
                var datePingInfo = new PingInfo();
                datePingInfo.DateSentInt = (uint)changeDetectionResult.IndexOfFirstDetection;
                analysisFeedback += $"Changes detected: first change at {datePingInfo.DateSent} ,number of changes {changeDetectionResult.NumberOfDetections}, Avg Respone Time: {changeDetectionResult.AverageScore:F2}, Certainty score : {changeDetectionResult.MinPValue:F2} (closer to zero is more certain). ";
            }
            if (isSpikeDetected)
            {
                var datePingInfo = new PingInfo();
                datePingInfo.DateSentInt = (uint)spikeDetectionResult.IndexOfFirstDetection;
                analysisFeedback += $"Spikes detected: first spike at {datePingInfo.DateSent}, number of spikes {spikeDetectionResult.NumberOfDetections}, Avg Response Time: {spikeDetectionResult.AverageScore:F2}, Certainty score: {spikeDetectionResult.MinPValue:F2} (closer to zero is more certain). ";
            }
        }
        else
        {
            analysisFeedback += "No significant issues detected.";
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
        int monitorIPID = monitorPingInfo.MonitorIPID;
        var detectionResult = new DetectionResult();
        try
        {
            if (TryReuseLatchedDetection(monitorPingInfo, isChange: true, out var latched))
                return latched;
            if (!CheckMonitorPingInfoOK(monitorPingInfo, monitorIPID, detectionResult))
            {
                return detectionResult;
            }
            var localPingInfos = GetLocalPingInfos(monitorPingInfo!);
            var config = _windowManager.GetConfig(monitorIPID);
            var changeParams = monitorPingInfo.EffectiveModelParameters ?? _mlParams.ActiveModelParameters;
            var changeConfidence = changeParams.ChangeConfidence;
            var changePreTrain = changeParams.ChangePreTrain;
            var changeWindow = changeParams.PredictWindow > 0 ? changeParams.PredictWindow : config.ChangeWindow;
            changeWindow = Math.Max(1, changeWindow);
            var changeMaxWindow = Math.Max(config.MaxChangeWindow, changeWindow);
            changePreTrain = Math.Clamp(changePreTrain, 1, Math.Max(1, changeWindow - 1));

            _logger.LogDebug("Window config before change detection monitor={Monitor} changeWindow={ChangeWindow} changePreTrain={ChangePreTrain} spikeWindow={SpikeWindow} spikePreTrain={SpikePreTrain}", monitorIPID, changeWindow, changePreTrain, config.SpikeWindow, config.SpikePreTrain);

            localPingInfos = TrimWindow(localPingInfos, changeWindow, changeMaxWindow, changePreTrain);
            await EnsureModelInitialized(monitorIPID, "Change", changeConfidence, changePreTrain);
            detectionResult = PredictForHostChange(localPingInfos, monitorIPID);
            _logger.LogDebug($"Change detection for MonitorPingInfoID {monitorPingInfo.ID}");
        }
        catch (Exception e)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Could not run InitSpikeDetection for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
            return detectionResult;
        }
        return detectionResult;
    }
    public async Task<DetectionResult> InitSpikeDetection(MonitorPingInfo monitorPingInfo)
    {
        int monitorIPID = monitorPingInfo.MonitorIPID;
        var detectionResult = new DetectionResult();
        try
        {
            if (TryReuseLatchedDetection(monitorPingInfo, isChange: false, out var latched))
                return latched;
            if (!CheckMonitorPingInfoOK(monitorPingInfo, monitorIPID, detectionResult))
            {
                return detectionResult;
            }
            var originalLocalPingInfos = GetLocalPingInfos(monitorPingInfo!);
            var config = _windowManager.GetConfig(monitorIPID);
            var spikeParams = monitorPingInfo.EffectiveModelParameters ?? _mlParams.ActiveModelParameters;
            var spikeConfidence = spikeParams.SpikeConfidence;
            var spikePreTrain = spikeParams.SpikePreTrain;
            var spikeWindow = spikeParams.PredictWindow > 0 ? spikeParams.PredictWindow : config.SpikeWindow;
            spikeWindow = Math.Max(1, spikeWindow);
            var spikeMaxWindow = Math.Max(config.MaxSpikeWindow, spikeWindow);
            var spikeThreshold = spikeParams.SpikeDetectionThreshold > 0 ? spikeParams.SpikeDetectionThreshold : SpikeDetectionThreshold;
            spikePreTrain = Math.Clamp(spikePreTrain, 1, Math.Max(1, spikeWindow - 1));

            _logger.LogDebug("Window config before spike detection monitor={Monitor} changeWindow={ChangeWindow} changePreTrain={ChangePreTrain} spikeWindow={SpikeWindow} spikePreTrain={SpikePreTrain}", monitorIPID, config.ChangeWindow, config.ChangePreTrain, spikeWindow, spikePreTrain);

            var trimmedLocalPingInfos = TrimWindow(originalLocalPingInfos, spikeWindow, spikeMaxWindow, spikePreTrain);
            await EnsureModelInitialized(monitorIPID, "Spike", spikeConfidence, spikePreTrain);
            detectionResult = PredictForHostSpike(trimmedLocalPingInfos, monitorIPID, spikeThreshold);

            bool trimmedApplied = trimmedLocalPingInfos.Count != originalLocalPingInfos.Count;
            if (!detectionResult.IsIssueDetected
                && detectionResult.NumberOfDetections > 0
                && trimmedApplied
                && config.SpikeWindow < config.MaxSpikeWindow)
            {
                await EnsureModelInitialized(monitorIPID, "Spike", spikeConfidence, spikePreTrain);
                detectionResult = PredictForHostSpike(originalLocalPingInfos, monitorIPID, spikeThreshold);
            }
            _logger.LogDebug($"Spike detection for MonitorPingInfoID {monitorPingInfo.ID}");
        }
        catch (Exception e)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Could not run InitSpikeDetection for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
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
            _logger.LogDebug("Skipping {Mode} detection for monitor {Monitor} because AlertSent is true", isChange ? "change" : "spike", monitorPingInfo.MonitorIPID);
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
    public DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos, int monitorIPID)
    {
        var result = new DetectionResult();
        var modelType = "Change"; // Define model type
        var key = (monitorIPID, modelType);
        if (!_models.TryGetValue(key, out var model))
        {
            throw new InvalidOperationException($"Model for MonitorIPID {monitorIPID} and ModelType {modelType} not found.");
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
        var changeParams = _hostParameters.TryGetValue(monitorIPID, out var changeResolved)
            ? changeResolved
            : _mlParams.ActiveModelParameters;
        LogMlnetDiagnostics("change", monitorIPID, predictions, localPingInfos, changeParams.ChangePreTrain);
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? $"An issue was detected at {dateOfDetection}" : "No issues detected")} with {result.NumberOfDetections} number of detections.";
        result.Result.Success = true;
        return result;
    }
    public DetectionResult PredictForHostChange(LocalPingInfo input, int monitorIPID)
    {
        var result = new DetectionResult();
        var modelType = "Change";
        var key = (monitorIPID, modelType);
        if (!_models.TryGetValue(key, out var model))
        {
            throw new InvalidOperationException($"Model for MonitorIPID {monitorIPID} and ModelType {modelType} not found.");
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
    public DetectionResult PredictForHostSpike(LocalPingInfo input, int monitorIPID)
    {
        var result = new DetectionResult();
        var modelType = "Spike";
        var key = (monitorIPID, modelType);
        if (!_models.TryGetValue(key, out var model))
        {
            throw new InvalidOperationException($"Model for MonitorIPID {monitorIPID} and ModelType {modelType} not found.");
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
    public DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos, int monitorIPID, int spikeDetectionThreshold)
    {
        var result = new DetectionResult();
        var modelType = "Spike"; // Define model type
        var key = (monitorIPID, modelType);
        if (!_models.TryGetValue(key, out var model))
        {
            throw new InvalidOperationException($"Model for MonitorIPID {monitorIPID} and ModelType {modelType} not found.");
        }
        var predictions = model.PredictList(localPingInfos).ToList();
        result.NumberOfDetections = predictions.Count(p => p.Prediction[0] == 1);
        result.IsIssueDetected = result.NumberOfDetections > spikeDetectionThreshold;
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
        var resolvedParams = _hostParameters.TryGetValue(monitorIPID, out var hostParams)
            ? hostParams
            : _mlParams.ActiveModelParameters;
        LogMlnetDiagnostics("spike", monitorIPID, predictions, localPingInfos, resolvedParams.SpikePreTrain);
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? $"An issue was detected at {dateOfDetection}" : "No issues detected")} with {result.NumberOfDetections} number of detections.";
        result.Result.Success = true;
        return result;
    }
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
