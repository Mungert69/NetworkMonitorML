using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using System.Threading.Tasks;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Data;
using NetworkMonitor.ML.Data;
using NetworkMonitor.ML.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using NetworkMonitor.ML.Repository;


namespace NetworkMonitor.ML.Services;

public interface IMonitorMLService
{

    Task Init();
    Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj);
    Task<List<LocalPingInfo>> TrainForHost(int monitorPingInfoID);
    DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos, int monitorIPID);
    DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos, int monitorIPID);
    Task<DetectionResult> InitChangeDetection(int monitorIPID, int dataSetID);
    Task<DetectionResult> InitSpikeDetection(int monitorIPID, int dataSetID);

    Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> CheckHost(int monitorIPID, int dataSetID);
    int PredictWindow { get; set; }

}


public class MonitorMLService : IMonitorMLService
{
    private Dictionary<(int monitorIPID, string modelType), IMLModel> _models = new Dictionary<(int monitorIPID, string modelType), IMLModel>();

    private ILogger _logger;
    private IRabbitRepo _rabbitRepo;

    private int _issueThreshold = 3;

    private int _predictWindow = 300;
    //private IServiceScopeFactory _scopeFactory;
    private readonly IMLModelFactory _mlModelFactory;
    private readonly IMonitorMLDataRepo _monitorMLDataRepo;

    private DeviationAnalyzer _deviationAnalyzer = new DeviationAnalyzer(10, 1);

    public int PredictWindow { get => _predictWindow; set => _predictWindow = value; }

    public MonitorMLService(ILogger<MonitorMLService> logger, IMonitorMLDataRepo monitorMLDataRepo, IMLModelFactory mlModelFactory, IRabbitRepo rabbitRepo)
    {
        _logger = logger;
        //_scopeFactory = scopeFactory;
        _mlModelFactory = mlModelFactory;
        _monitorMLDataRepo = monitorMLDataRepo;
        _rabbitRepo = rabbitRepo;
    }
    public async Task Init()
    {
        await ProcessAllHosts();
    }
    private async Task EnsureModelInitialized(int monitorIPID, string modelType, double confidence)
    {
        var key = (monitorIPID, modelType);

        if (!_models.ContainsKey(key))
        {
            await GetOrCreateModel(monitorIPID, modelType, confidence);
        }
    }

    private async Task<IMLModel> GetOrCreateModel(int monitorIPID, string modelType, double confidence)
    {
        var key = (monitorIPID, modelType);

        if (!_models.TryGetValue(key, out var model))
        {
            model = _mlModelFactory.CreateModel(modelType, monitorIPID, confidence);
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
    ResultObj result = new ResultObj();
    
    try
    {
        // Assuming there's a method to get the latest MonitorPingInfos with a specified window size
        // This method needs to be implemented in the IMonitorMLDataRepo and MonitorMLDataRepo
        var latestMonitorPingInfos = await _monitorMLDataRepo.GetLatestMonitorPingInfos(_predictWindow);

        if (latestMonitorPingInfos == null || !latestMonitorPingInfos.Any())
        {
            result.Success = false;
            result.Message = "No latest MonitorPingInfo records found.";
            return result;
        }

        foreach (var monitorPingInfo in latestMonitorPingInfos)
        {
            var checkHostResult = await CheckHost(monitorPingInfo);
            if (checkHostResult.Success)
            {
                // Assuming the UpdateMonitorPingInfoWithPredictionResultsById method accepts PredictStatus directly
                // You might need to adjust the method signature or the way you're updating the MonitorPingInfo
                await _monitorMLDataRepo.UpdateMonitorPingInfoWithPredictionResultsById(monitorPingInfo.MonitorIPID, monitorPingInfo.DataSetID, new PredictStatus
                {
                    Message=result.Message,
                    ChangeDetectionResult = checkHostResult.Data.ChangeResult,
                    SpikeDetectionResult = checkHostResult.Data.SpikeResult
                });
            }
        }

            // Publish the updated MonitorPingInfos
            // You might need to adjust this part to fit your actual publishing logic
            await PublishRepo.MonitorPingInfos(_logger, _rabbitRepo, latestMonitorPingInfos, "test", "testkey");
        result.Success = true;
        result.Message = "Successfully processed and published latest MonitorPingInfos.";
    }
    catch (Exception ex)
    {
        result.Success = false;
        result.Message = $"Error in CheckLatestHosts: {ex.Message}";
        _logger.LogError(result.Message);
    }

    return result;
}

    public async Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> CheckHost(MonitorPingInfo monitorPingInfo)
    {
        var result = new TResultObj<(DetectionResult changeDetectionResult, DetectionResult spikeDetectionResult)>();

        if (monitorPingInfo != null)
        {
            
                int monitorIPID = monitorPingInfo.MonitorIPID;
                int dataSetID = monitorPingInfo.DataSetID;

                var changeDetectionResult = await InitChangeDetection(monitorIPID, dataSetID);
                var spikeDetectionResult = await InitSpikeDetection(monitorIPID, dataSetID);

                var combinedAnalysis = AnalyzeResults(changeDetectionResult, spikeDetectionResult);
                result.Success = changeDetectionResult.Result.Success && spikeDetectionResult.Result.Success;
                result.Message = combinedAnalysis;
                result.Data = (changeDetectionResult, spikeDetectionResult);
                _logger.LogDebug($"Combined analysis for MonitorIPID {monitorIPID}: {combinedAnalysis}");
                var predictStatus = new PredictStatus();
                predictStatus.ChangeDetectionResult = changeDetectionResult;
                predictStatus.SpikeDetectionResult = spikeDetectionResult;
                //   var monitorPingInfo = await _monitorMLDataRepo.GetMonitorPingInfo(monitorIPID, dataSetID);

                predictStatus.EventTime = monitorPingInfo.DateEnded;
                if (changeDetectionResult.IsIssueDetected || spikeDetectionResult.IsIssueDetected)
                {
                    _logger.LogInformation($"MonitorPingInfo: {monitorPingInfo.ID} - {combinedAnalysis}");
                }
                predictStatus.Message = combinedAnalysis;

                await _monitorMLDataRepo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, predictStatus);

            }
            else {
                result.Success = false;
                result.Message = " monitorPingInfo is null";
            }
            return result;

        }
        public async Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> CheckHost(int monitorIPID, int dataSetID)
        {
            var monitorPingInfo = await _monitorMLDataRepo.GetMonitorPingInfo(monitorIPID, dataSetID);
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
                    analysisFeedback += $"Changes detected: {changeDetectionResult.NumberOfDetections}, Avg Score: {changeDetectionResult.AverageScore:F2}, Min P-Value: {changeDetectionResult.MinPValue:F2}. ";
                }
                if (isSpikeDetected)
                {
                    analysisFeedback += $"Spikes detected: {spikeDetectionResult.NumberOfDetections}, Avg Score: {spikeDetectionResult.AverageScore:F2}, Min P-Value: {spikeDetectionResult.MinPValue:F2}. ";
                }
            }
            else
            {
                analysisFeedback += "No significant issues detected.";
            }

            // Adding Martingale value analysis if relevant
            if (maxMartingaleValue > _issueThreshold)
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
        public async Task<DetectionResult> InitChangeDetection(int monitorIPID, int dataSetID)
        {
            var detectionResult = new DetectionResult();
            try
            {
                var monitorPingInfo = await _monitorMLDataRepo.GetMonitorPingInfo(monitorIPID, PredictWindow, dataSetID);
                if (!CheckMonitorPingInfoOK(monitorPingInfo, monitorIPID, detectionResult))
                {
                    return detectionResult;
                }

                var localPingInfos = GetLocalPingInfos(monitorPingInfo!);
                await EnsureModelInitialized(monitorIPID, "Change", 98d);
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

        public async Task<DetectionResult> InitSpikeDetection(int monitorIPID, int dataSetID)
        {
            var detectionResult = new DetectionResult();
            try
            {
                var monitorPingInfo = await _monitorMLDataRepo.GetMonitorPingInfo(monitorIPID, PredictWindow, dataSetID);
                if (!CheckMonitorPingInfoOK(monitorPingInfo, monitorIPID, detectionResult))
                {
                    return detectionResult;
                }
                var localPingInfos = GetLocalPingInfos(monitorPingInfo!);
                await EnsureModelInitialized(monitorIPID, "Spike", 99d);
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
            //var localPingInfos = new List<LocalPingInfo>();

            var localPingInfos = await _monitorMLDataRepo.GetLocalPingInfosForHost(monitorIPID);

            if (localPingInfos.Count > 0)
            {
                //_mlModel.Train(localPingInfos);
                _logger.LogDebug($"MLSERVICE : Training PingInfo Data for host {monitorIPID}.");
            }

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

            result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? "An issue was detected" : "No issues detected")} with {result.NumberOfDetections} number of detections.";
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
            result.IsIssueDetected = result.NumberOfDetections > 10;

            // Check if there are any detections before calculating average and minimum
            if (result.NumberOfDetections > 0)
            {
                result.AverageScore = predictions.Where(p => p.Prediction[0] == 1).Average(p => p.Prediction[1]);
                result.MinPValue = predictions.Where(p => p.Prediction[0] == 1).Min(p => p.Prediction[2]);
            }

            // For Spike Detection, if there's no Martingale value, adjust this section accordingly
            result.Result.Message = $"Success: Ran OK. {(result.IsIssueDetected ? "An issue was detected" : "No issues detected")} with {result.NumberOfDetections} number of detections.";
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

    }



