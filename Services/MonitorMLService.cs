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
    bool PredictForHost(List<LocalPingInfo> newPingInfos);
}


public class MonitorMLService : IMonitorMLService
{
    private IMLModel _mlModel;
    private ILogger _logger;
    private IServiceScopeFactory _scopeFactory;

    private DeviationAnalyzer _deviationAnalyzer = new DeviationAnalyzer(10, 1);

    public MonitorMLService(ILogger<MonitorMLService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task Init()
    {

        using (var scope = _scopeFactory.CreateScope())
        {
            MonitorContext monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var monitorPingInfos = await monitorContext.MonitorPingInfos
                                      .AsNoTracking()
                                      .Include(e => e.PingInfos)
                                      .Where(w => w.Enabled).Take(1)
                                      .ToListAsync();

            foreach (var monitorPingInfo in monitorPingInfos)
            {
                var localPingInfos = monitorPingInfo.PingInfos.Select(pi => new LocalPingInfo
                {
                    DateSentInt = pi.DateSentInt,
                    RoundTripTime = (ushort)(pi.RoundTripTime ?? 0),
                    StatusID = pi.StatusID
                }).ToList();

                // Change Detection Model
                _mlModel = new ChangeDetectionModel(monitorPingInfo.ID);
                _mlModel.Train(localPingInfos);
                bool isDataUnusualForChangeDetection = PredictForHost(localPingInfos);
                _logger.LogInformation($"Change detection for MonitorPingInfoID {monitorPingInfo.ID}, data unusual: {isDataUnusualForChangeDetection}");

                // Spike Detection Model
                _mlModel = new SpikeDetectionModel(monitorPingInfo.ID);
                _mlModel.Train(localPingInfos);
                bool isDataUnusualForSpikeDetection = PredictForHost(localPingInfos);
                _logger.LogInformation($"Spike detection for MonitorPingInfoID {monitorPingInfo.ID}, data unusual: {isDataUnusualForSpikeDetection}");
            }
        }
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

    public bool PredictForHost(List<LocalPingInfo> localPingInfos)
    {

        var predictions = _mlModel.PredictList(localPingInfos);
        _mlModel.PrintPrediction(predictions);
        return predictions.Any(p => p.Prediction[0] == 1);

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
