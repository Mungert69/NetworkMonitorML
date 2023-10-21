using Microsoft.ML;
using Microsoft.ML.TimeSeries;
using Microsoft.ML.Data;
using System;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using System.Threading.Tasks;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Data;
using Microsoft.Extensions.DependencyInjection;

namespace NetworkMonitor.ML.Services;

public interface IMonitorMLService
{
    Task Init();
    Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj);
    void Train();
    void Predict();
}


public class MonitorMLService : IMonitorMLService
{
    private ILogger _logger;
    private IServiceScopeFactory _scopeFactory;
    public MonitorMLService(ILogger<MonitorMLService> logger, IServiceScopeFactory scopeFactory)
    {

        _logger = logger;
        _scopeFactory = scopeFactory;

    }

    public async Task Init() { }

    public void Train()
    {
        var trainer = new Trainer("model.zip");
        using (var scope = _scopeFactory.CreateScope())
        {
            MonitorContext monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            trainer.Train(monitorContext); _logger.LogDebug("MLSERVICE : Training PingInfo Data from database.");
        }

    }

    public void Predict()
    {
        // Make predictions
        var predictor = new Predictor("path_to_saved_model.zip");
        var sampleData = new LocalPingInfo { DateSentInt = 1234 }; // Replace with actual data
        var predictedRoundTripTime = predictor.Predict(sampleData);
        _logger.LogInformation($"Predicted Round Trip Time: {predictedRoundTripTime}");


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
