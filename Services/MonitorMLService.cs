using Microsoft.ML;
using System;
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
    DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos, int monitorIPID);
    Task<DetectionResult> InitChangeDetection(MonitorPingInfo monitorPingInfo);
    Task<DetectionResult> InitSpikeDetection(MonitorPingInfo monitorPingInfo);

    Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> CheckHost(int monitorIPID, int dataSetID);
    Task<TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>>> CheckLatestHostsTest();
    Task<ResultObj> CheckLatestHosts();
    ResultObj UpdatePingInfos(ProcessorDataObj processorDataObj);

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

    private DeviationAnalyzer _deviationAnalyzer = new DeviationAnalyzer(10, 1);

    public int PredictWindow { get => _mlParams.PredictWindow; set => _mlParams.PredictWindow = value; }
    public int MartingaleDetectionThreshold { get => _martingaleDetectionThreshold; set => _martingaleDetectionThreshold = value; }
    public int SpikeDetectionThreshold { get => _mlParams.SpikeDetectionThreshold; set => _mlParams.SpikeDetectionThreshold = value; }
    public double SpikeConfidence { get => _mlParams.SpikeConfidence; set => _mlParams.SpikeConfidence = value; }
    public double ChangeConfidence { get => _mlParams.ChangeConfidence; set => _mlParams.ChangeConfidence = value; }
    public int ChangePreTrain { get => _mlParams.ChangePreTrain; set => _mlParams.ChangePreTrain = value; }
    public int SpikePreTrain { get => _mlParams.SpikePreTrain; set => _mlParams.SpikePreTrain = value; }

    public MonitorMLService(ILogger<MonitorMLService> logger, IMonitorMLDataRepo monitorMLDataRepo, IMLModelFactory mlModelFactory, IRabbitRepo rabbitRepo, ISystemParamsHelper systemParamsHelper)
    {
        _logger = logger;
        //_scopeFactory = scopeFactory;
        _mlModelFactory = mlModelFactory;
        _monitorMLDataRepo = monitorMLDataRepo;
        _rabbitRepo = rabbitRepo;
        _systemParams = systemParamsHelper.GetSystemParams();
        _mlParams = systemParamsHelper.GetMLParams();
        Init().Wait();
    }
    public async Task Init()
    {
        try
        {
            await _monitorMLDataRepo.GetLatestMonitorPingInfos(_mlParams.PredictWindow);
        }
        catch (Exception e) {
            _logger.LogCritical($" Error : unable to init Service . Error was : {e.Message}");
        }
    }
    private async Task EnsureModelInitialized(int monitorIPID, string modelType, double confidence, int preTrain)
    {
        var key = (monitorIPID, modelType);

        if (!_models.ContainsKey(key))
        {
            await GetOrCreateModel(monitorIPID, modelType, confidence, preTrain);
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
        TResultObj<List<TResultObj<(DetectionResult changeResult, DetectionResult SpikeResult)>>> testResult = await CheckLatestHostsTest();
        var result = new ResultObj();
        result.Success = testResult.Success;
        result.Message = testResult.Message;
        return result;
    }
    public async Task<TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>>> CheckLatestHostsTest()
    {
        TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>> result = new TResultObj<List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>>();
        result.Message = " SERVICE : CheckLatestHost : ";

        try
        {
            // Assuming there's a method to get the latest MonitorPingInfos with a specified window size
            // This method needs to be implemented in the IMonitorMLDataRepo and MonitorMLDataRepo

            var latestMonitorPingInfos = await _monitorMLDataRepo.GetLatestMonitorPingInfos(_mlParams.PredictWindow);

            if (latestMonitorPingInfos == null || !latestMonitorPingInfos.Any())
            {
                result.Success = false;
                result.Message = "No latest MonitorPingInfo records found.";
                return result;
            }
            var results = new List<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>>();
            foreach (var monitorPingInfo in latestMonitorPingInfos)
            {

                results.Add(await CheckHost(monitorPingInfo));

            }

            // Publish the updated MonitorPingInfos
            // You might need to adjust this part to fit your actual publishing logic
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
            result.Success = resultPublish.Success && results.Any(r => !r.Success);
            result.Message += resultPublish.Message;
            result.Data = results;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error in CheckLatestHosts: {ex.Message}";
            _logger.LogError(result.Message);
        }

        if (result.Success) _logger.LogInformation(result.Message);
        else _logger.LogError(result.Message);
        return result;
    }

    public async Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> CheckHost(MonitorPingInfo? monitorPingInfo)
    {
        var result = new TResultObj<(DetectionResult changeDetectionResult, DetectionResult spikeDetectionResult)>();

        if (monitorPingInfo != null)
        {

            int monitorIPID = monitorPingInfo.MonitorIPID;
            int dataSetID = monitorPingInfo.DataSetID;

            var changeDetectionResult = await InitChangeDetection(monitorPingInfo);
            var spikeDetectionResult = await InitSpikeDetection(monitorPingInfo);

            var combinedAnalysis = AnalyzeResults(changeDetectionResult, spikeDetectionResult);
            result.Success = changeDetectionResult.Result.Success && spikeDetectionResult.Result.Success;
            result.Message = combinedAnalysis;
            result.Data = (changeDetectionResult, spikeDetectionResult);
            _logger.LogDebug($"Combined analysis for MonitorIPID {monitorIPID}: {combinedAnalysis}");
            var predictStatus = new PredictStatus();
            predictStatus.ChangeDetectionResult = changeDetectionResult;
            predictStatus.SpikeDetectionResult = spikeDetectionResult;
            predictStatus.EventTime = monitorPingInfo.DateEnded;
            if (changeDetectionResult.IsIssueDetected || spikeDetectionResult.IsIssueDetected)
            {
                predictStatus.AlertFlag = true;
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

        if (isChangeDetected || isSpikeDetected)
        {
            analysisFeedback += "Possible issues detected. ";
            if (isChangeDetected)
            {
                analysisFeedback += $"Changes detected: first change at index {changeDetectionResult.IndexOfFirstDetection} ,number of changes {changeDetectionResult.NumberOfDetections}, Avg Score: {changeDetectionResult.AverageScore:F2}, Min P-Value: {changeDetectionResult.MinPValue:F2}. ";
            }
            if (isSpikeDetected)
            {
                analysisFeedback += $"Spikes detected: first spike at index {changeDetectionResult.IndexOfFirstDetection}, number of spikes {spikeDetectionResult.NumberOfDetections}, Avg Score: {spikeDetectionResult.AverageScore:F2}, Min P-Value: {spikeDetectionResult.MinPValue:F2}. ";
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

        if (monitorPingInfo.PingInfos.Count < PredictWindow)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : MonitorPingInfo with ID {monitorIPID} not enough data for prediction was retrieved . {PredictWindow - monitorPingInfo.PingInfos.Count} more events needs to make a prediction.";
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
            await EnsureModelInitialized(monitorIPID, "Change", _mlParams.SpikeConfidence, SpikePreTrain);
            detectionResult = PredictForHostSpike(input, monitorIPID);
        }
        catch (Exception e)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Could not run InitSpikeDetection for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
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
            detectionResult.Result.Message = $" Error : Could not run InitSpikeDetection for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
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
            if (!CheckMonitorPingInfoOK(monitorPingInfo, monitorIPID, detectionResult))
            {
                return detectionResult;
            }

            var localPingInfos = GetLocalPingInfos(monitorPingInfo!);
            await EnsureModelInitialized(monitorIPID, "Change", _mlParams.ChangeConfidence, ChangePreTrain);
            //_mlModel = _mlModelFactory.CreateModel("Change", monitorPingInfo!.ID, 90d);
            //_mlModel.Train(localPingInfos);
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
            if (!CheckMonitorPingInfoOK(monitorPingInfo, monitorIPID, detectionResult))
            {
                return detectionResult;
            }
            var localPingInfos = GetLocalPingInfos(monitorPingInfo!);
            await EnsureModelInitialized(monitorIPID, "Spike", _mlParams.SpikeConfidence, SpikePreTrain);
            //_mlModel = _mlModelFactory.CreateModel("Spike", monitorPingInfo!.ID, 99d);
            //_mlModel.Train(localPingInfos);
            detectionResult = PredictForHostSpike(localPingInfos, monitorIPID);
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

        // Check if there are any detections before calculating average and minimum
        if (result.NumberOfDetections > 0)
        {
            result.AverageScore = predictions.Where(p => p.Prediction[0] == 1).Average(p => p.Prediction[1]);
            result.MinPValue = predictions.Where(p => p.Prediction[0] == 1).Min(p => p.Prediction[2]);
        }

        // Ensure there are predictions before attempting to find the max Martingale value
        if (predictions.Any())
        {
            result.MaxMartingaleValue = predictions.Max(p => p.Prediction[3]);
        }
        int index = predictions.FindIndex(p => p.Prediction[0] == 1);
        result.IndexOfFirstDetection = index;
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? $"An issue was detected at index {index}" : "No issues detected")} with {result.NumberOfDetections} number of detections.";
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
        result.MaxMartingaleValue = prediction.Prediction[3];

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


        // Index of detection:
        result.IndexOfFirstDetection = result.IsIssueDetected ? 0 : -1;
        // 0 because it's the only input, -1 to signal no detection

        // Message
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? "An issue was detected." : "No issues detected.")}";
        result.Result.Success = true;


        return result;
    }


    public DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos, int monitorIPID)
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
        result.IsIssueDetected = result.NumberOfDetections > SpikeDetectionThreshold;

        // Check if there are any detections before calculating average and minimum
        if (result.NumberOfDetections > 0)
        {
            result.AverageScore = predictions.Where(p => p.Prediction[0] == 1).Average(p => p.Prediction[1]);
            result.MinPValue = predictions.Where(p => p.Prediction[0] == 1).Min(p => p.Prediction[2]);
        }


        int index = predictions.FindIndex(p => p.Prediction[0] == 1);
        result.IndexOfFirstDetection = index;
        result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? $"An issue was detected at index {index}" : "No issues detected")} with {result.NumberOfDetections} number of detections.";
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
        result.Message = "Service : UpdatePingInfos : For Processor AuthID "+processorDataObj.AppID;

        try
        {
            if (processorDataObj.MonitorPingInfos != null)
            {
                foreach (var monitorPingInfo in processorDataObj.MonitorPingInfos)
                {
                    monitorPingInfo.PingInfos = processorDataObj.PingInfos.Where(w => w.MonitorPingInfoID == monitorPingInfo.ID).ToList();
                    monitorPingInfo.DataSetID = 0;
                    var updateResult = _monitorMLDataRepo.UpdateMonitorPingInfo(monitorPingInfo);

                    if (!updateResult.Success) {
                        result.Message += updateResult.Message;
                        return result; }
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
}



