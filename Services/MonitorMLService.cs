using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using System.Threading.Tasks;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Data;
using NetworkMonitor.ML.Data;
using NetworkMonitor.ML.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;


namespace NetworkMonitor.ML.Services;

public interface IMonitorMLService
{

    Task Init();
    Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj);
    Task<List<LocalPingInfo>> TrainForHost(int monitorPingInfoID);
    DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos);
    DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos);
    Task<DetectionResult> InitChangeDetection(int monitorIPID);
    Task<DetectionResult> InitSpikeDetection(int monitorIPID);

     Task<ResultObj> CheckHost(int monitorIPID);
int PredictWindow { get ; set ; }

}


public class MonitorMLService : IMonitorMLService
{
    private IMLModel _mlModel;
    private ILogger _logger;
    private int _issueThreshold = 3;

    private int _predictWindow = 300;
    //private IServiceScopeFactory _scopeFactory;
     private readonly IMLModelFactory _mlModelFactory;
    private readonly IMonitorMLDataRepo _monitorMLDataRepo;

    private DeviationAnalyzer _deviationAnalyzer = new DeviationAnalyzer(10, 1);

    public int PredictWindow { get => _predictWindow; set => _predictWindow = value; }

    public MonitorMLService(ILogger<MonitorMLService> logger, IMonitorMLDataRepo monitorMLDataRepo, IMLModelFactory mlModelFactory)
    {
        _logger = logger;
        //_scopeFactory = scopeFactory;
        _mlModelFactory = mlModelFactory;
        _monitorMLDataRepo = monitorMLDataRepo;
    }
    public async Task Init()
    {
       
         }

    public async Task<ResultObj> CheckHost(int monitorIPID) {
        var result = new ResultObj();

        var changeDetectionResult = await InitChangeDetection(monitorIPID);
        var spikeDetectionResult = await InitSpikeDetection(monitorIPID);

        var combinedAnalysis = AnalyzeResults(changeDetectionResult, spikeDetectionResult);
        result.Success = changeDetectionResult.Result.Success && spikeDetectionResult.Result.Success;
        result.Message = combinedAnalysis;
        result.Data = (ChangeResult: changeDetectionResult, SpikeResult: spikeDetectionResult);
        _logger.LogInformation($"Combined analysis for MonitorIPID {monitorIPID}: {combinedAnalysis}");
        return result;
  
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

    if (!changeDetectionResult.Result.Success || !spikeDetectionResult.Result.Success) {
        if (!changeDetectionResult.Result.Success) analysisFeedback += $" Change Detection failed with Message: {changeDetectionResult.Result.Message}.";
        if (!spikeDetectionResult.Result.Success) analysisFeedback += $" Spike Detection failed with Message: {spikeDetectionResult.Result.Message}.";
        return analysisFeedback;
    }

    if (isChangeDetected || isSpikeDetected) {
        analysisFeedback += "Possible issues detected. ";
        if (isChangeDetected) {
            analysisFeedback += $"Changes detected: {changeDetectionResult.NumberOfDetections}, Avg Score: {changeDetectionResult.AverageScore:F2}, Min P-Value: {changeDetectionResult.MinPValue:F2}. ";
        }
        if (isSpikeDetected) {
            analysisFeedback += $"Spikes detected: {spikeDetectionResult.NumberOfDetections}, Avg Score: {spikeDetectionResult.AverageScore:F2}, Min P-Value: {spikeDetectionResult.MinPValue:F2}. ";
        }
    } else {
        analysisFeedback += "No significant issues detected.";
    }

    // Adding Martingale value analysis if relevant
    if (maxMartingaleValue > _issueThreshold) { // Use a defined threshold based on your requirements
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
        if (monitorPingInfo.PingInfos == null) { 
              detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Host with ID {monitorIPID} contains no event data.";
            return false;
        }

        if (monitorPingInfo.PingInfos.Count < PredictWindow)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : MonitorPingInfo with ID {monitorIPID} not enough data for prediction was retrieved . { PredictWindow-monitorPingInfo.PingInfos.Count} more events needs to make a prediction.";
            return false;
        }
        return true;
    }
    public async Task<DetectionResult> InitChangeDetection(int monitorIPID)
    {
        var detectionResult = new DetectionResult();
        try
        {
              var monitorPingInfo = await _monitorMLDataRepo.GetMonitorPingInfo(monitorIPID, PredictWindow);
                if (!CheckMonitorPingInfoOK( monitorPingInfo,monitorIPID, detectionResult)) {
                    return detectionResult;
                }

                var localPingInfos = GetLocalPingInfos(monitorPingInfo!);
      
                _mlModel = _mlModelFactory.CreateChangeDetectionModel(monitorPingInfo!.ID, 90d);
                //_mlModel.Train(localPingInfos);
                detectionResult = PredictForHostChange(localPingInfos);


                _logger.LogInformation($"Change detection for MonitorPingInfoID {monitorPingInfo.ID}");

            
        }
        catch (Exception e)
        {
            detectionResult.Result.Success = false;
            detectionResult.Result.Message = $" Error : Could not run InitSpikeDetection for MonitorPingInfo with ID {monitorIPID} . Error was : {e.Message}";
            return detectionResult;
        }
        return detectionResult;
    }

    public async Task<DetectionResult> InitSpikeDetection(int monitorIPID)
    {
        var detectionResult = new DetectionResult();
        try
        {
             var monitorPingInfo = await _monitorMLDataRepo.GetMonitorPingInfo( monitorIPID, PredictWindow);
                 if (!CheckMonitorPingInfoOK( monitorPingInfo,monitorIPID, detectionResult)) {
                    return detectionResult;
                }
                var localPingInfos = GetLocalPingInfos(monitorPingInfo!);

                _mlModel = _mlModelFactory.CreateSpikeDetectionModel(monitorPingInfo!.ID, 99d);
                //_mlModel.Train(localPingInfos);
                detectionResult = PredictForHostSpike(localPingInfos);
                _logger.LogInformation($"Spike detection for MonitorPingInfoID {monitorPingInfo.ID}");

            

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


    public async Task <List<LocalPingInfo>> TrainForHost(int monitorPingInfoID)
    {
        //var localPingInfos = new List<LocalPingInfo>();

        var localPingInfos = await _monitorMLDataRepo.GetLocalPingInfosForHost(monitorPingInfoID);

            if (localPingInfos.Count > 0)
            {
                _mlModel.Train(localPingInfos);
                _logger.LogDebug($"MLSERVICE : Training PingInfo Data for host {monitorPingInfoID}.");
            }
        
        return localPingInfos;
    }



  public DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos)
{
    var result = new DetectionResult();
    var predictions = _mlModel.PredictList(localPingInfos).ToList();

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

 public DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos)
{
    var result = new DetectionResult();
    var predictions = _mlModel.PredictList(localPingInfos).ToList();

    result.IsIssueDetected = predictions.Any(p => p.Prediction[0] == 1);
    result.NumberOfDetections = predictions.Count(p => p.Prediction[0] == 1);

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

public class DetectionResult
{
    public bool IsIssueDetected { get; set; }
    public int NumberOfDetections { get; set; }
    public bool IsDataLimited { get; set; } = false;
    public double AverageScore { get; set; }
    public double MinPValue { get; set; }
    public double MaxMartingaleValue { get; set; }
    public ResultObj Result { get; set; } = new ResultObj();
    // Additional fields as required
}


