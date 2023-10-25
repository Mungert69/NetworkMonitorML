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
        var monitorPingInfoID=191531;
        _mlModel = new ChangeDetectionModel(monitorPingInfoID);
        var localPingInfos = TrainForHost(monitorPingInfoID);
        Random rnd = new Random();
        for (int i = 0; i < 50; i++) // Generating 100 test ping infos
        {
            localPingInfos.Add(new LocalPingInfo
            {
                DateSentInt = (uint)DateTime.UtcNow.AddMilliseconds(-i).Ticks, // Just an example, adjust as needed
                RoundTripTime = (ushort)(rnd.NextDouble() * 10 + 100), // Random value between 1 and 3
                StatusID = 1 // Assuming status ID is 1 for all, adjust as needed
            });
        }
        for (int i = 50; i < 100; i++) // Generating 100 test ping infos
        {
            localPingInfos.Add(new LocalPingInfo
            {
                DateSentInt = (uint)DateTime.UtcNow.AddMilliseconds(-i).Ticks, // Just an example, adjust as needed
                RoundTripTime = (ushort)(rnd.NextDouble() * 10 + 20), // Random value between 1 and 3
                StatusID = 1 // Assuming status ID is 1 for all, adjust as needed
            });
        }
        _logger.LogInformation($" Running change detection got prediction is data unusual {PredictForHost(localPingInfos)}");
           _mlModel = new SpikeDetectionModel(monitorPingInfoID);
            localPingInfos = TrainForHost(monitorPingInfoID);
        rnd = new Random();
        for (int i = 0; i < 50; i++) // Generating 100 test ping infos
        {
            localPingInfos.Add(new LocalPingInfo
            {
                DateSentInt = (uint)DateTime.UtcNow.AddMilliseconds(-i).Ticks, // Just an example, adjust as needed
                RoundTripTime = (ushort)(rnd.NextDouble() * 3 + 1), // Random value between 1 and 3
                StatusID = 1 // Assuming status ID is 1 for all, adjust as needed
            });
        }
         localPingInfos.Add(new LocalPingInfo
            {
                DateSentInt = (uint)DateTime.UtcNow.AddMilliseconds(-50).Ticks, // Just an example, adjust as needed
                RoundTripTime = (ushort)(rnd.NextDouble() * 5 + 200), // Random value between 1 and 3
                StatusID = 1 // Assuming status ID is 1 for all, adjust as needed
            });
        for (int i = 51; i < 100; i++) // Generating 100 test ping infos
        {
            localPingInfos.Add(new LocalPingInfo
            {
                DateSentInt = (uint)DateTime.UtcNow.AddMilliseconds(-i).Ticks, // Just an example, adjust as needed
                RoundTripTime = (ushort)(rnd.NextDouble() * 3 + 1), // Random value between 1 and 3
                StatusID = 1 // Assuming status ID is 1 for all, adjust as needed
            });
        }
          _logger.LogInformation($" Running spike detection got prediction is data unusual {PredictForHost(localPingInfos)}");
      
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
