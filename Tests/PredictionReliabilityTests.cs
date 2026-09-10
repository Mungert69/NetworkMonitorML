using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkMonitor.Data;
using NetworkMonitor.ML.Data;
using NetworkMonitor.ML.Model;
using NetworkMonitor.ML.Services;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Utils.Helpers;
using Xunit;

namespace NetworkMonitor.MonitorML.Tests;

public class PredictionReliabilityTests
{
    [Fact]
    public void RelativeShiftUsesObservedLatencyRatherThanForecast()
    {
        // A healthy forecast must not hide an unexpected 1,000 ms response.
        Assert.Equal(9d, TimesFmRabbitModel.RelativeShift(1000, 100));
        Assert.Equal(0d, TimesFmRabbitModel.RelativeShift(100, 100));
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(41, 40, 1)]
    public void ForecastIndexUsesEffectivePretrain(int observationIndex, int effectivePreTrain, int expected)
        => Assert.Equal(expected, TimesFmRabbitModel.ForecastIndex(observationIndex, effectivePreTrain));

    [Fact]
    public void TimesFmReturnsNeutralPredictionsWithoutCallingRabbitForEmptyTimeoutOnlyOrOnePointInputs()
    {
        var repo = new Mock<IRabbitRepo>();
        using var model = new TimesFmRabbitModel(repo.Object, new SystemUrl(),
            NullLogger<TimesFmRabbitModel>.Instance, 1, .8, 40, "Change", "");
        foreach (var samples in new[]
        {
            new List<LocalPingInfo>(),
            new List<LocalPingInfo> { new() { RoundTripTime = ushort.MaxValue } },
            new List<LocalPingInfo> { new() { RoundTripTime = 100 } },
            new List<LocalPingInfo> { new() { RoundTripTime = ushort.MaxValue }, new() { RoundTripTime = 100 } }
        })
        {
            var predictions = model.PredictList(samples).ToList();
            Assert.Equal(samples.Count, predictions.Count);
            Assert.All(predictions, p => Assert.Equal(AnomalyPrediction.Neutral().Prediction, p.Prediction));
        }
        Assert.Empty(repo.Invocations);
    }

    private static MonitorPingInfo Host(int id, int count) => new()
    {
        ID = id, MonitorIPID = id, DataSetID = 0, Enabled = true, PredictStatus = new(),
        PingInfos = Enumerable.Range(1, count).Select(i => new PingInfo
        { DateSentInt = (uint)i, RoundTripTime = 100 }).ToList()
    };

    private static MonitorMLService Service(Mock<IMonitorMLDataRepo> repo, Mock<IRabbitRepo> rabbit,
        Mock<ILogger<MonitorMLService>>? logger = null)
    {
        var helper = new Mock<ISystemParamsHelper>();
        helper.Setup(h => h.GetSystemParams()).Returns(new SystemParams { ServiceID = "test" });
        helper.Setup(h => h.GetMLParams()).Returns(new MLParams
        {
            PredictWindow = 120, ChangePreTrain = 60, SpikePreTrain = 45,
            ActiveModelParameters = new ResolvedModelParameters
            { PredictWindow = 120, ChangePreTrain = 60, SpikePreTrain = 45, SpikeDetectionThreshold = 1 }
        });
        var model = new Mock<IMLModel>();
        model.Setup(m => m.PredictList(It.IsAny<List<LocalPingInfo>>()))
            .Returns((List<LocalPingInfo> samples) => samples.Select(_ => new AnomalyPrediction
            { Prediction = new[] { 1d, 100d, .01d, 1d } }).ToArray());
        var factory = new Mock<IMLModelFactory>();
        factory.Setup(f => f.CreateModel(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns(model.Object);
        return new MonitorMLService(logger?.Object ?? NullLogger<MonitorMLService>.Instance,
            repo.Object, factory.Object, rabbit.Object, helper.Object);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedSaveIsLoggedAndNeverPublishedAsAlert(bool throws, bool includeHealthy)
    {
        var failed = Host(1, 120);
        var healthy = Host(2, 120);
        var repo = new Mock<IMonitorMLDataRepo>();
        repo.Setup(r => r.GetLatestMonitorPingInfos(It.IsAny<int>()))
            .ReturnsAsync(includeHealthy ? new List<MonitorPingInfo> { failed, healthy } : new List<MonitorPingInfo> { failed });
        repo.Setup(r => r.UpdateMonitorPingInfoWithPredictionResultsById(2, 0, It.IsAny<PredictStatus>()))
            .ReturnsAsync(new ResultObj { Success = true });
        var save = repo.Setup(r => r.UpdateMonitorPingInfoWithPredictionResultsById(1, 0, It.IsAny<PredictStatus>()));
        if (throws) save.ThrowsAsync(new InvalidOperationException("database unavailable"));
        else save.ReturnsAsync(new ResultObj { Success = false, Message = "database unavailable" });
        var rabbit = new Mock<IRabbitRepo>();
        var logger = new Mock<ILogger<MonitorMLService>>();
        var service = Service(repo, rabbit, logger);
        var result = await service.CheckLatestHostsTest();
        Assert.False(result.Success);
        Assert.False(result.Data![0].Success);
        if (includeHealthy)
        {
            Assert.True(result.Data[1].Success);
            var published = Assert.Single(rabbit.Invocations, i => i.Method.Name == "PublishJsonZAsync");
            var alerts = Assert.IsType<ProcessorDataObj>(published.Arguments[1]).PredictStatusAlerts;
            Assert.Equal(2, Assert.Single(alerts).ID);
        }
        else Assert.DoesNotContain(rabbit.Invocations, i => i.Method.Name == "PublishJsonZAsync");
        Assert.Contains(logger.Invocations, i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Error);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(300)]
    public async Task CacheRetainsRequestedUsableWindowAndDetectionRuns(int window)
    {
        var host = Host(1, window);
        host.ModelConfig = new MonitorModelConfig { PredictWindow = window };
        var cache = new MonitorMLDataRepo(NullLogger<MonitorMLDataRepo>.Instance, new Mock<IServiceScopeFactory>().Object);
        typeof(MonitorMLDataRepo).GetField("_windowSize", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, 120);
        typeof(MonitorMLDataRepo).GetField("_isDataFull", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, true);
        Assert.True(cache.UpdateMonitorPingInfo(host).Success);
        var incoming = Host(1, 0);
        incoming.PingInfos.Add(new PingInfo { DateSentInt = (uint)(window + 1), RoundTripTime = ushort.MaxValue });
        Assert.True(cache.UpdateMonitorPingInfo(incoming).Success);
        Assert.Equal(window + 1, host.PingInfos.Count);
        var repo = new Mock<IMonitorMLDataRepo>();
        repo.Setup(r => r.UpdateMonitorPingInfoWithPredictionResultsById(1, 0, It.IsAny<PredictStatus>()))
            .ReturnsAsync(new ResultObj { Success = true });
        var result = await Service(repo, new Mock<IRabbitRepo>()).CheckHost(host);
        Assert.True(result.Success, result.Message);
        Assert.False(result.Data.ChangeResult.IsDataLimited);
        Assert.False(result.Data.SpikeResult.IsDataLimited);
    }

    [Fact]
    public void LongOutageRetentionIsBoundedAndDoesNotKeepAncientGoodData()
    {
        var samples = Host(1, 120).PingInfos;
        samples.AddRange(Enumerable.Range(121, 600).Select(i => new PingInfo { DateSentInt = (uint)i, RoundTripTime = ushort.MaxValue }));
        var trimmed = PredictionWindow.Trim(samples, 120, p => p.RoundTripTime < ushort.MaxValue);
        Assert.Equal(480, trimmed.Count);
        Assert.All(trimmed, p => Assert.Equal(ushort.MaxValue, p.RoundTripTime));
    }

    [Fact]
    public void PredictionWindowKeepsNewestUsableSamplesAndTheirInterveningTimeouts()
    {
        var samples = new List<LocalPingInfo>
        {
            new() { DateSentInt = 1, RoundTripTime = 10 },
            new() { DateSentInt = 2, RoundTripTime = 20 },
            new() { DateSentInt = 3, RoundTripTime = ushort.MaxValue },
            new() { DateSentInt = 4, RoundTripTime = 30 },
            new() { DateSentInt = 5, RoundTripTime = ushort.MaxValue },
            new() { DateSentInt = 6, RoundTripTime = 40 }
        };
        var retained = PredictionWindow.Trim(samples, 3, point => !point.IsTimeout());
        Assert.Equal(new uint[] { 2, 3, 4, 5, 6 }, retained.Select(p => p.DateSentInt));
        Assert.Equal(3, retained.Count(p => !p.IsTimeout()));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(120, 480)]
    [InlineData(300, 1200)]
    public void PredictionWindowCapacityHasBoundedTimeoutHeadroom(int window, int expectedCapacity)
        => Assert.Equal(expectedCapacity, PredictionWindow.Capacity(window));

    [Fact]
    public void PredictionWindowDropsOnlyTheOldestUsableSamplesBeyondItsTarget()
    {
        var samples = Enumerable.Range(1, 6).Select(i => new LocalPingInfo
        { DateSentInt = (uint)i, RoundTripTime = i }).ToList();
        var retained = PredictionWindow.Trim(samples, 3, point => !point.IsTimeout());
        Assert.Equal(new uint[] { 4, 5, 6 }, retained.Select(p => p.DateSentInt));
    }

    [Fact]
    public void DuplicateIncomingPingTimestampsAreNotAddedTwice()
    {
        var cached = Host(1, 1);
        var repository = new MonitorMLDataRepo(NullLogger<MonitorMLDataRepo>.Instance, new Mock<IServiceScopeFactory>().Object);
        typeof(MonitorMLDataRepo).GetField("_isDataFull", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(repository, true);
        Assert.True(repository.UpdateMonitorPingInfo(cached).Success);
        var update = Host(1, 0);
        update.PingInfos.AddRange(new[]
        {
            new PingInfo { DateSentInt = 2, RoundTripTime = 100 },
            new PingInfo { DateSentInt = 2, RoundTripTime = 200 }
        });
        Assert.True(repository.UpdateMonitorPingInfo(update).Success);
        Assert.Equal(new uint[] { 1, 2 }, cached.PingInfos.Select(p => p.DateSentInt));
    }

    [Fact]
    public async Task CachedMlnetModelsAreRecreatedWhenResolvedSettingsChange()
    {
        var host = Host(1, 120);
        host.ModelConfig = new MonitorModelConfig { ChangePreTrain = 30 };
        var repo = new Mock<IMonitorMLDataRepo>();
        repo.Setup(r => r.UpdateMonitorPingInfoWithPredictionResultsById(1, 0, It.IsAny<PredictStatus>()))
            .ReturnsAsync(new ResultObj { Success = true });
        var helper = new Mock<ISystemParamsHelper>();
        helper.Setup(h => h.GetSystemParams()).Returns(new SystemParams());
        helper.Setup(h => h.GetMLParams()).Returns(new MLParams
        {
            PredictWindow = 120, ChangePreTrain = 60, SpikePreTrain = 45,
            ActiveModelParameters = new ResolvedModelParameters
            { PredictWindow = 120, ChangePreTrain = 60, SpikePreTrain = 45, SpikeDetectionThreshold = 1 }
        });
        var model = new Mock<IMLModel>();
        model.SetupAllProperties();
        model.Setup(m => m.PredictList(It.IsAny<List<LocalPingInfo>>()))
            .Returns((List<LocalPingInfo> values) => values.Select(_ => AnomalyPrediction.Neutral()).ToArray());
        var factory = new Mock<IMLModelFactory>();
        factory.Setup(f => f.CreateModel(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns(model.Object);
        var service = new MonitorMLService(NullLogger<MonitorMLService>.Instance, repo.Object, factory.Object,
            new Mock<IRabbitRepo>().Object, helper.Object);

        await service.CheckHost(host);
        host.ModelConfig.ChangePreTrain = 35;
        await service.CheckHost(host);

        factory.Verify(f => f.CreateModel("Change", 1, It.IsAny<double>(), 30), Times.Once);
        factory.Verify(f => f.CreateModel("Change", 1, It.IsAny<double>(), 35), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimeoutsDoNotChangeMlnetPredictionsOrCreateSpikes(bool change)
    {
        var directory = Directory.CreateTempSubdirectory("ml-timeout-regression-");
        try
        {
            var path = Path.Combine(directory.FullName, "model.zip");
            var clean = Enumerable.Range(0, 120).Select(i => new LocalPingInfo { RoundTripTime = 100 + i % 7 }).ToList();
            var withTimeouts = clean.ToList();
            withTimeouts.Insert(10, new LocalPingInfo { RoundTripTime = ushort.MaxValue });
            withTimeouts.Insert(70, new LocalPingInfo { RoundTripTime = ushort.MaxValue });
            Func<List<LocalPingInfo>, IEnumerable<AnomalyPrediction>> predict = change
                ? new ChangeDetectionModel.Predictor(path, new MLContext(), 90, 40).GetDeviations
                : new SpikeDetectionModel.Predictor(path, new MLContext(), 90, 40).GetDeviations;
            var expected = predict(clean).SelectMany(p => p.Prediction).ToArray();
            var actual = predict(withTimeouts).ToList();
            Assert.Equal(expected, actual.Where((_, i) => !withTimeouts[i].IsTimeout()).SelectMany(p => p.Prediction).ToArray());
            Assert.Equal(AnomalyPrediction.Neutral().Prediction, actual[10].Prediction);
            Assert.Equal(AnomalyPrediction.Neutral().Prediction, actual[70].Prediction);
        }
        finally { Directory.Delete(directory.FullName, recursive: true); }
    }

    [Fact]
    public async Task DatabaseLoadBackfillsUsableHistoryAcrossDatasets()
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<MonitorContext>(o => o.UseInMemoryDatabase(name, db => db.EnableNullChecks(false)));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorContext>();
        var live = Host(1, 120);
        live.PingInfos[0].RoundTripTime = ushort.MaxValue;
        var old = Host(2, 0);
        old.MonitorIPID = 1;
        old.DataSetID = 1;
        old.PingInfos.Add(new PingInfo { DateSentInt = 0, RoundTripTime = 100 });
        db.MonitorPingInfos.AddRange(live, old);
        await db.SaveChangesAsync();
        var repo = new MonitorMLDataRepo(NullLogger<MonitorMLDataRepo>.Instance, provider.GetRequiredService<IServiceScopeFactory>());
        var loaded = await repo.GetDBWithContextMonitorPingInfo(1, 120, 0, db);
        Assert.NotNull(loaded);
        Assert.Equal(121, loaded.PingInfos.Count);
        Assert.Equal(120, loaded.PingInfos.Count(p => p.RoundTripTime < ushort.MaxValue));
    }
}
