using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using System.Threading.Tasks;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Data;
using NetworkMonitor.ML.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;


namespace NetworkMonitor.ML.Services;

public interface IMonitorMLService
{

    Task Init();
    Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj);
    List<LocalPingInfo> TrainForHost(int monitorPingInfoID);
    DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos);
    DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos);
    Task<DetectionResult> InitChangeDetection(int monitorIPID);
    Task<DetectionResult> InitSpikeDetection(int monitorIPID);


}


public class MonitorMLService : IMonitorMLService
{
    private IMLModel _mlModel;
    private ILogger _logger;
    private int _issueThreshold = 3;

    private int predictWindow = 300;
    private IServiceScopeFactory _scopeFactory;

    private DeviationAnalyzer _deviationAnalyzer = new DeviationAnalyzer(10, 1);

    public MonitorMLService(ILogger<MonitorMLService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }
    public async Task Init()
    {
        int monitorIPID = 1;
        var changeDetectionResult = await InitChangeDetection(monitorIPID);
        var spikeDetectionResult = await InitSpikeDetection(monitorIPID);

        var combinedAnalysis = AnalyzeResults(changeDetectionResult, spikeDetectionResult);
        _logger.LogInformation($"Combined analysis for MonitorIPID {monitorIPID}: {combinedAnalysis}");
    }

    private string AnalyzeResults(DetectionResult changeDetectionResult, DetectionResult spikeDetectionResult)
    {
        // Combining results from both models
        bool isChangeDetected = changeDetectionResult.IsIssueDetected;
        bool isSpikeDetected = spikeDetectionResult.IsIssueDetected;

        int totalDetections = changeDetectionResult.NumberOfDetections + spikeDetectionResult.NumberOfDetections;
        double maxMartingaleValue = Math.Max(changeDetectionResult.MartingaleValue, spikeDetectionResult.MartingaleValue);

        // Analysis logic to give feedback
        string analysisFeedback = "Analysis: ";

        if (isChangeDetected && isSpikeDetected)
        {
            analysisFeedback += $"High likelihood of issues. Detected changes: {changeDetectionResult.NumberOfDetections}, spikes: {spikeDetectionResult.NumberOfDetections}.";
        }
        else if (isChangeDetected || isSpikeDetected)
        {
            analysisFeedback += $"Possible issues detected. ";
            analysisFeedback += isChangeDetected ? $"Changes detected: {changeDetectionResult.NumberOfDetections}. " : "";
            analysisFeedback += isSpikeDetected ? $"Spikes detected: {spikeDetectionResult.NumberOfDetections}. " : "";
        }
        else
        {
            analysisFeedback += "No significant issues detected.";
        }

        // Adding Martingale value analysis if relevant
        if (maxMartingaleValue > _issueThreshold) // Define someThreshold based on your requirements
        {
            analysisFeedback += $" Martingale value is high: {maxMartingaleValue}, indicating a sudden change.";
        }

        return analysisFeedback;
    }

    public async Task<MonitorPingInfo> GetMonitorPingInfo(MonitorContext monitorContext, int monitorIPID, int windowSize)
{
    var latestMonitorPingInfo = await monitorContext.MonitorPingInfos
        .Include(mpi => mpi.PingInfos)
        .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == 0);

    int additionalPingInfosNeeded = windowSize - latestMonitorPingInfo.PingInfos.Count;

    if (additionalPingInfosNeeded > 0)
    {
        var previousDataSetID = await monitorContext.MonitorPingInfos
            .Where(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID != 0)
            .MaxAsync(mpi => mpi.DataSetID);

        var previousMonitorPingInfo = await monitorContext.MonitorPingInfos
            .Include(mpi => mpi.PingInfos)
            .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == previousDataSetID);

        var additionalPingInfos = previousMonitorPingInfo.PingInfos
            .OrderByDescending(pi => pi.DateSentInt)
            .Take(additionalPingInfosNeeded)
            .ToList();

        latestMonitorPingInfo.PingInfos.AddRange(additionalPingInfos);
    }

    latestMonitorPingInfo.PingInfos = latestMonitorPingInfo.PingInfos
        .OrderBy(pi => pi.DateSentInt)
        .ToList();

    return latestMonitorPingInfo;
}

    public async Task<DetectionResult> InitChangeDetection(int monitorIPID)
    {
        var detectionResult = new DetectionResult();

        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var monitorPingInfo = await GetMonitorPingInfo(monitorContext, monitorIPID, predictWindow);


            var localPingInfos = GetLocalPingInfos(monitorPingInfo);

            _mlModel = new ChangeDetectionModel(monitorPingInfo.ID, 90d);
            //_mlModel.Train(localPingInfos);
            detectionResult = PredictForHostChange(localPingInfos);

            _logger.LogInformation($"Change detection for MonitorPingInfoID {monitorPingInfo.ID}");

        }
        return detectionResult;
    }

    public async Task<DetectionResult> InitSpikeDetection(int monitorIPID)
    {
        var detectionResult = new DetectionResult();
        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var monitorPingInfo = await GetMonitorPingInfo(monitorContext, monitorIPID, predictWindow);

            var localPingInfos = GetLocalPingInfos(monitorPingInfo);

            _mlModel = new SpikeDetectionModel(monitorPingInfo.ID, 99d);
            //_mlModel.Train(localPingInfos);
            detectionResult = PredictForHostSpike(localPingInfos);
            _logger.LogInformation($"Spike detection for MonitorPingInfoID {monitorPingInfo.ID}");

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


    public List<LocalPingInfo> TrainForHost(int monitorPingInfoID)
    {
        var localPingInfos = new List<LocalPingInfo>();

        using (var scope = _scopeFactory.CreateScope())
        {
            MonitorContext monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            localPingInfos = monitorContext.PingInfos
                .Where(p => p.MonitorPingInfoID == monitorPingInfoID)
                .ToList().Select(p => new LocalPingInfo
                {
                    DateSentInt = p.DateSentInt,
                    RoundTripTime = p.RoundTripTime ?? 0,
                    StatusID = p.StatusID
                    // ... add other properties if needed ...
                }).ToList();

            if (localPingInfos.Count > 0)
            {
                _mlModel.Train(localPingInfos);
                _logger.LogDebug($"MLSERVICE : Training PingInfo Data for host {monitorPingInfoID}.");
            }
        }
        return localPingInfos;
    }



    public DetectionResult PredictForHostChange(List<LocalPingInfo> localPingInfos)
    {
        var result = new DetectionResult();
        var predictions = _mlModel.PredictList(localPingInfos);
        _mlModel.PrintPrediction(predictions);
        // Analyze predictions
        result.IsIssueDetected = predictions.Any(p => p.Prediction[0] == 1);
        result.NumberOfDetections = predictions.Count(p => p.Prediction[0] == 1);
        //result.MartingaleValue = predictions.Max(p => p.Prediction[3]); // Assuming the Martingale value is at index 3

        return result;

    }
    public DetectionResult PredictForHostSpike(List<LocalPingInfo> localPingInfos)
    {
        var result = new DetectionResult();
        var predictions = _mlModel.PredictList(localPingInfos);
        _mlModel.PrintPrediction(predictions);
        // Analyze predictions
        result.IsIssueDetected = predictions.Any(p => p.Prediction[0] == 1);
        result.NumberOfDetections = predictions.Count(p => p.Prediction[0] == 1);

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
    public double MartingaleValue { get; set; }
    // You can add more fields as required
}

