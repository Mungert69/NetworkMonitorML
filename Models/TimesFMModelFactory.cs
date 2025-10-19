// File: ML/TimesFmModelFactory.cs
using Microsoft.Extensions.Logging;
using NetworkMonitor.ML.Model;
using NetworkMonitor.Objects.Factory;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Objects;

namespace NetworkMonitor.ML.Model;

public sealed class TimesFmModelFactory : IMLModelFactory
{
    private readonly IRabbitRepo _rabbitRepo;
    private readonly SystemUrl _sys;
    private readonly ILoggerFactory _lf;
    private readonly MLParams _mlParams;

    // optional sharding key for your Rabbit topology
    private readonly string _routingKey;

    public TimesFmModelFactory(IRabbitRepo rabbitRepo, SystemParams systemParams, ILoggerFactory lf, MLParams mlParams)
    {
        _rabbitRepo = rabbitRepo;
        _sys = systemParams.ThisSystemUrl;
        _lf = lf;
        _routingKey = systemParams.RabbitRoutingKey; // set from config if you use shards
        _mlParams = mlParams;
    }

    public IMLModel CreateModel(string modelType, int monitorPingInfoID, double confidence, int preTrain)
    {
        var log = _lf.CreateLogger<TimesFmRabbitModel>();
        // both "change" and "spike" use the same adapter; service logic differs later
        return new TimesFmRabbitModel(
            _rabbitRepo,
            _sys,
            log,
            monitorPingInfoID,
            confidence,
            preTrain,
            modelType,
            _routingKey,
            _mlParams.ActiveModelParameters.TimesFmSettings);
    }
}
