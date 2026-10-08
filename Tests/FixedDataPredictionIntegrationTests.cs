using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

// Only the external forecast response and outgoing broker are substituted.
// These tests run the production service, detectors, cache and repository.
[Trait("Category", "FixedDataIntegration")]
public class FixedDataPredictionIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public sealed class Scenario
    {
        public string Name { get; set; } = "";
        public string Backend { get; set; } = "";
        public int[] Readings { get; set; } = Array.Empty<int>();
        public bool Change { get; set; }
        public bool Spike { get; set; }
        public int ChangeCount { get; set; }
        public int SpikeCount { get; set; }
        public int? FirstDetectionIndex { get; set; }
        public MonitorModelConfig Config { get; set; } = new();
        public string Forecast { get; set; } = "normal";
    }

    private static List<Scenario> Scenarios() => JsonSerializer.Deserialize<List<Scenario>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tests", "Fixtures", "prediction-scenarios.json")), Json)!;
    private static Scenario Fixture(string name) => Scenarios().Single(s => s.Name == name);
    public static IEnumerable<object[]> ScenarioNames() => Scenarios().Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task FixedSamplesProduceExpectedDetectionsSavedStateAndPublishedStatus(string name)
    {
        var scenario = Fixture(name);
        using var harness = await Harness.Create(scenario.Readings, scenario.Backend == "ML.NET", scenario.Config);
        harness.Forecast.Response = scenario.Forecast;
        var result = await harness.Run();
        Assert.True(result.Success, result.Message);
        Assert.Equal(scenario.Change, result.Data.ChangeResult.IsIssueDetected);
        Assert.Equal(scenario.Spike, result.Data.SpikeResult.IsIssueDetected);
        Assert.Equal(scenario.ChangeCount, result.Data.ChangeResult.NumberOfDetections);
        Assert.Equal(scenario.SpikeCount, result.Data.SpikeResult.NumberOfDetections);
        await harness.AssertSavedAndPublished(scenario.Change && scenario.Spike, result.Data.ChangeResult, result.Data.SpikeResult);
        if (scenario.FirstDetectionIndex.HasValue)
        {
            Assert.Equal(Timestamp(scenario.FirstDetectionIndex.Value), result.Data.ChangeResult.IndexOfFirstDetection);
            Assert.Equal(Timestamp(scenario.FirstDetectionIndex.Value), result.Data.SpikeResult.IndexOfFirstDetection);
        }

        if (scenario.Backend == "TimesFM")
        {
            // Check the request envelope, usable-point filtering, every historical
            // prefix and correspondence of each forecast to its observed point.
            Assert.Equal(2, harness.Forecast.Requests.Count);
            var usable = scenario.Readings.Where(v => v < ushort.MaxValue).Select(v => (double)v).ToArray();
            foreach (var request in harness.Forecast.Requests)
            {
                int preTrain = request.Mode == "Change"
                    ? scenario.Config.ChangePreTrain ?? 4 : scenario.Config.SpikePreTrain ?? 4;
                Assert.Equal(usable.Length - preTrain, request.Prefixes.Count);
                for (int i = 0; i < request.Prefixes.Count; i++)
                    Assert.Equal(usable.Take(i + preTrain), request.Prefixes[i]);
            }
            if (name == "timeouts_preserve_detection_alignment")
            {
                var expected = Timestamp(12); // Third usable shifted sample.
                Assert.Equal(expected, result.Data.ChangeResult.IndexOfFirstDetection);
                Assert.Equal(expected, result.Data.SpikeResult.IndexOfFirstDetection);
            }
        }
        else Assert.Empty(harness.Forecast.Requests);
    }

    [Fact]
    public async Task FirstPredictionCreatesStatusWithCorrectDecisionAndCount()
    {
        using var harness = await Harness.Create(Fixture("sustained_shift").Readings, existingStatus: false);
        var result = await harness.Run();
        Assert.True(result.Success, result.Message);
        await harness.AssertSavedAndPublished(true, result.Data.ChangeResult, result.Data.SpikeResult);
    }

    [Fact]
    public async Task DeletedDatabaseHostMakesSaveFailAndPreventsPublication()
    {
        using var harness = await Harness.Create(Fixture("sustained_shift").Readings);
        await harness.DeleteLiveHost();
        var result = await harness.Run();
        Assert.False(result.Success);
        Assert.Contains("Error saving predictions", result.Message);
        Assert.Empty(harness.Published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMlnetAndTimesFmAdapterConfirmOrVetoHybridAlert(bool veto)
    {
        var fixture = Fixture("mlnet_spike_and_change");
        using var harness = await Harness.Create(fixture.Readings, mlnet: true, hybrid: true);
        harness.Forecast.Response = veto ? "wide-band" : "normal";
        var result = await harness.Run();
        Assert.True(result.Success, result.Message);
        Assert.NotEmpty(harness.Forecast.Requests);
        Assert.Contains(veto ? "TimesFM vetoed alert" : "TimesFM confirmed alert", result.Message);
        await harness.AssertSavedAndPublished(!veto, result.Data.ChangeResult, result.Data.SpikeResult);
    }

    [Fact]
    public async Task HybridDoesNotRequestForecastWhenRealMlnetPrimaryGateIsNotMet()
    {
        using var harness = await Harness.Create(Fixture("mlnet_spikes").Readings, mlnet: true, hybrid: true);
        var result = await harness.Run();
        Assert.True(result.Success, result.Message);
        Assert.Empty(harness.Forecast.Requests);
        Assert.Contains("primary gate not met", result.Message);
        await harness.AssertSavedAndPublished(false, result.Data.ChangeResult, result.Data.SpikeResult);
    }

    [Fact]
    public async Task InsufficientUsableHistorySkipsForecastAndDoesNotPublish()
    {
        using var harness = await Harness.Create(new[] { 100, 100, 100, 100, 100, 100,
            65535, 65535, 65535, 65535, 65535, 65535 });
        var result = await harness.Run();
        Assert.False(result.Success);
        Assert.True(result.Data.ChangeResult.IsDataLimited);
        Assert.True(result.Data.SpikeResult.IsDataLimited);
        Assert.Empty(harness.Forecast.Requests);
        Assert.Empty(harness.Published);
        Assert.False((await harness.StoredStatus()).AlertFlag);
    }

    [Fact]
    public async Task TooFewTotalSamplesFailEvaluationAndAreNotPublished()
    {
        using var harness = await Harness.Create(new[] { 100, 100, 100 });
        var result = await harness.Service.CheckHost(harness.HostId, 0);
        Assert.False(result.Success);
        Assert.Empty(harness.Forecast.Requests);
        Assert.Empty(harness.Published);
        Assert.False((await harness.StoredStatus()).AlertFlag);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("request-timeout")]
    [InlineData("request-failure")]
    public async Task ForecastFailuresNeverBecomePublishedAlerts(string response)
    {
        using var harness = await Harness.Create(Fixture("sustained_shift").Readings);
        harness.Forecast.Response = response;
        var result = await harness.Run();
        Assert.False(result.Success);
        Assert.False(result.Data.ChangeResult.Result.Success);
        Assert.False(result.Data.SpikeResult.Result.Success);
        Assert.Empty(harness.Published);
        Assert.False((await harness.StoredStatus()).AlertFlag);
    }

    [Fact]
    public async Task SavedConfidenceAndDetectorOverridesTakeEffectThenRemovalRestoresDefaults()
    {
        var readings = Fixture("outside_narrow_confidence").Readings;
        using var harness = await Harness.Create(readings);
        var initial = await harness.Run();
        await harness.AssertSavedAndPublished(false, initial.Data.ChangeResult, initial.Data.SpikeResult);
        await harness.Configure(config =>
        {
            config.ChangeConfidence = 0.6; // Historical fractional confidence.
            config.SpikeConfidence = 60;
            config.MinRelShift = 0.05;
            config.SpikeRunLength = 5;
        });
        var overridden = await harness.Run();
        Assert.True(overridden.Data.ChangeResult.IsIssueDetected);
        Assert.False(overridden.Data.SpikeResult.IsIssueDetected);
        await harness.AssertSavedAndPublished(false, overridden.Data.ChangeResult, overridden.Data.SpikeResult);
        await harness.Configure(config => config.SpikeRunLength = null);
        var aligned = await harness.Run();
        await harness.AssertSavedAndPublished(true, aligned.Data.ChangeResult, aligned.Data.SpikeResult);
        await harness.RemoveConfig();
        var restored = await harness.Run();
        await harness.AssertSavedAndPublished(false, restored.Data.ChangeResult, restored.Data.SpikeResult);
    }

    [Fact]
    public async Task SentAlertLatchesResultsUntilResetAndHealthyDataThenClearsState()
    {
        using var harness = await Harness.Create(Fixture("sustained_shift").Readings);
        var first = await harness.Run();
        await harness.AssertSavedAndPublished(true, first.Data.ChangeResult, first.Data.SpikeResult);
        Assert.All(await harness.Service.UpdateAlertSent(new List<int> { harness.HostId }, true), result => Assert.True(result.Success));
        int requests = harness.Forecast.Requests.Count;
        await harness.Append(Enumerable.Repeat(100, 60).ToArray());
        var latched = await harness.Run();
        Assert.Equal(requests, harness.Forecast.Requests.Count);
        Assert.Contains("Skipped run: alert already sent", latched.Data.ChangeResult.Result.Message);
        await harness.AssertSavedAndPublished(true, latched.Data.ChangeResult, latched.Data.SpikeResult);
        Assert.True((await harness.StoredStatus()).AlertSent);
        Assert.All(await harness.Service.ResetAlerts(new List<int> { harness.HostId }), result => Assert.True(result.Success));
        var reset = await harness.StoredStatus();
        Assert.False(reset.AlertFlag);
        Assert.False(reset.AlertSent);
        Assert.Equal(0, reset.DownCount);
        var resetMessage = Assert.Single(harness.Resets);
        Assert.Equal("fixture-predict", resetMessage.AppID);
        Assert.Equal(harness.HostId, Assert.Single(resetMessage.AlertFlagObjs).ID);
        var recovered = await harness.Run();
        Assert.True(harness.Forecast.Requests.Count > requests);
        await harness.AssertSavedAndPublished(false, recovered.Data.ChangeResult, recovered.Data.SpikeResult);
        Assert.False((await harness.StoredStatus()).AlertSent);
    }

    [Fact]
    public async Task LargerSavedWindowBackfillsHistoricalDatasetBeforeScoring()
    {
        using var harness = await Harness.Create(Enumerable.Repeat(100, 12).ToArray());
        await harness.AddHistorical(Enumerable.Repeat(100, 12).ToArray());
        await harness.Run();
        await harness.Configure(config => config.PredictWindow = 24);
        int before = harness.Forecast.Requests.Count;
        var result = await harness.Run();
        Assert.True(result.Success, result.Message);
        Assert.All(harness.Forecast.Requests.Skip(before), request => Assert.Equal(20, request.Prefixes.Count));
        var cached = Assert.Single(await harness.Repository.GetLatestMonitorPingInfos(12));
        Assert.Equal(24, cached.PingInfos.Count);
        Assert.Equal(cached.PingInfos.OrderBy(p => p.DateSentInt).Select(p => p.DateSentInt), cached.PingInfos.Select(p => p.DateSentInt));
        await harness.AssertSavedAndPublished(false, result.Data.ChangeResult, result.Data.SpikeResult);
    }

    private static int Timestamp(int index) => checked((int)new DateTimeOffset(Epoch.AddMinutes(index)).ToUnixTimeSeconds());

    private sealed class ForecastFixture : ISecondaryModelFactory, IDisposable
    {
        public sealed record Request(string Mode, List<double[]> Prefixes);
        public List<Request> Requests { get; } = new();
        public string Response { get; set; } = "normal";
        private readonly List<TimesFmRabbitModel> _models = new();
        public IMLModel CreateModel(string modelType, int id, double confidence, int preTrain)
        {
            var model = new TimesFmRabbitModel(new Mock<IRabbitRepo>().Object, new SystemUrl(),
                NullLogger<TimesFmRabbitModel>.Instance, id, confidence, preTrain, modelType, "",
                responseReader: (request, cancellation) => Respond(modelType, request, cancellation));
            _models.Add(model);
            return model;
        }

        private Task<string> Respond(string mode, string request, CancellationToken cancellation)
        {
            Assert.True(cancellation.CanBeCanceled);
            using var envelope = JsonDocument.Parse(request);
            Assert.Equal("google/timesfm-2.5-200m-pytorch", envelope.RootElement.GetProperty("model").GetString());
            using var payload = JsonDocument.Parse(envelope.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Assert.Equal(1, payload.RootElement.GetProperty("horizon").GetInt32());
            Assert.True(payload.RootElement.GetProperty("quantiles").GetBoolean());
            Assert.Equal(4096, payload.RootElement.GetProperty("max_context").GetInt32());
            var prefixes = payload.RootElement.GetProperty("series").EnumerateArray()
                .Select(row => row.EnumerateArray().Select(value => value.GetDouble()).ToArray()).ToList();
            Requests.Add(new Request(mode, prefixes));
            if (Response == "request-timeout") throw new OperationCanceledException(cancellation);
            if (Response == "request-failure") throw new IOException("Fixture forecast unavailable");
            if (Response == "malformed") return Task.FromResult("{\"forecast\":{\"bad\":true}}");
            double[] quantiles = Response == "wide-band"
                ? new[] { 100d, 0, 10, 20, 30, 100, 500, 1000, 1500, 2000 }
                : new[] { 100d, 90, 92, 94, 96, 100, 104, 106, 108, 110 };
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                horizon = 1,
                forecast = prefixes.Select((_, index) => new[] { Response == "varying" ? 100d + index * 10 : 100d }).ToArray(),
                quantiles = Response == "missing-quantiles" ? null : prefixes.Select((_, index) =>
                    Response == "varying" ? new[] { 0d, -2, -1.5, -1, -.5, 0, .5, 1, 1.5, 2 }
                        .Select(offset => 100d + index * 10 + offset).ToArray() : quantiles).ToArray()
            }));
        }
        public void Dispose() { foreach (var model in _models) model.Dispose(); }
    }

    private sealed class Harness : IDisposable
    {
        private static int _nextId = 200000;
        public int HostId { get; } = Interlocked.Increment(ref _nextId);
        public ForecastFixture Forecast { get; } = new();
        public List<ProcessorDataObj> Published { get; } = new();
        public List<AlertServiceAlertObj> Resets { get; } = new();
        public MonitorMLDataRepo Repository { get; private set; } = null!;
        public MonitorMLService Service { get; private set; } = null!;
        private readonly ServiceProvider _provider;
        private int _nextSample;

        private Harness()
        {
            var services = new ServiceCollection();
            services.AddDbContext<MonitorContext>(options => options.UseInMemoryDatabase(
                "fixed-data-" + HostId, database => database.EnableNullChecks(false)));
            _provider = services.BuildServiceProvider();
        }

        public static async Task<Harness> Create(int[] readings, bool mlnet = false,
            MonitorModelConfig? custom = null, bool hybrid = false, bool existingStatus = true)
        {
            var harness = new Harness();
            var parameters = mlnet ? MonitorMLTestData.GetMLParams().ActiveModelParameters.Clone() : new ResolvedModelParameters
            {
                PredictWindow = 12, ChangePreTrain = 4, SpikePreTrain = 4,
                ChangeConfidence = 80, SpikeConfidence = 80, SpikeDetectionThreshold = 1
            };
            // The ML.NET fixtures intentionally evaluate the complete 420-point
            // series, including spikes and the transition in its middle.
            if (mlnet) parameters.PredictWindow = readings.Length;
            var settings = new TimesFmResolvedSettings
            {
                RunLength = 3, KOfNK = 6, KOfNN = 12, MinRelShift = 0.20,
                MadAlpha = 0, MinBandAbs = 2, MinBandRel = 0, BaselineWindow = 120,
                RollSigmaWindow = 60, SigmaCooldown = 3, SampleRows = 0
            };
            parameters.TimesFmChangeSettings = settings.Clone();
            parameters.TimesFmSpikeSettings = settings.Clone();
            using (var scope = harness._provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MonitorContext>();
                db.MonitorIPs.Add(new MonitorIP { ID = harness.HostId, ModelConfig = custom });
                var host = harness.Host(readings, 0);
                host.PredictStatus = existingStatus ? new PredictStatus { MonitorPingInfoID = harness.HostId } : null;
                db.MonitorPingInfos.Add(host);
                await db.SaveChangesAsync();
            }
            harness._nextSample = readings.Length;
            harness.Repository = new MonitorMLDataRepo(NullLogger<MonitorMLDataRepo>.Instance,
                harness._provider.GetRequiredService<IServiceScopeFactory>());
            var helper = new Mock<ISystemParamsHelper>();
            helper.Setup(h => h.GetSystemParams()).Returns(new SystemParams { ServiceID = "fixture-predict" });
            helper.Setup(h => h.GetMLParams()).Returns(new MLParams
            {
                PredictWindow = parameters.PredictWindow, ChangePreTrain = parameters.ChangePreTrain,
                SpikePreTrain = parameters.SpikePreTrain, ChangeConfidence = parameters.ChangeConfidence,
                SpikeConfidence = parameters.SpikeConfidence, SpikeDetectionThreshold = parameters.SpikeDetectionThreshold,
                ActiveModelParameters = parameters,
                SecondaryModelSelection = hybrid ? "TimesFM" : null,
                SecondaryModelParameters = parameters.Clone()
            });
            var rabbit = new Mock<IRabbitRepo>();
            rabbit.Setup(r => r.PublishJsonZAsync("alertUpdatePredictStatusAlerts", It.IsAny<ProcessorDataObj>(), It.IsAny<string>()))
                .Callback<string, ProcessorDataObj, string>((_, message, _) => harness.Published.Add(
                    JsonSerializer.Deserialize<ProcessorDataObj>(JsonSerializer.Serialize(message))!))
                .ReturnsAsync("fixture-publication");
            rabbit.Setup(r => r.PublishAsync("alertMessageResetPredictAlerts", It.IsAny<AlertServiceAlertObj>(), It.IsAny<string>()))
                .Callback<string, AlertServiceAlertObj, string>((_, message, _) => harness.Resets.Add(
                    JsonSerializer.Deserialize<AlertServiceAlertObj>(JsonSerializer.Serialize(message))!))
                .Returns(Task.CompletedTask);
            harness.Service = new MonitorMLService(NullLogger<MonitorMLService>.Instance, harness.Repository,
                mlnet ? new MLModelFactory() : harness.Forecast, rabbit.Object, helper.Object,
                hybrid ? harness.Forecast : null);
            return harness;
        }

        private MonitorPingInfo Host(int[] readings, int start) => new()
        {
            ID = HostId, MonitorIPID = HostId, DataSetID = 0, Enabled = true,
            UserID = "fixture-user", Address = "fixture.example", EndPointType = "icmp",
            DateEnded = Epoch.AddMinutes(start + readings.Length),
            PingInfos = readings.Select((value, index) => new PingInfo
            { DateSentInt = (uint)Timestamp(start + index), RoundTripTime = (ushort)value, StatusID = 0 }).ToList()
        };

        public async Task<TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>> Run()
        {
            var batch = await Service.CheckLatestHostsTest();
            Assert.NotNull(batch.Data);
            var host = Assert.Single(batch.Data);
            Assert.Equal(host.Success, batch.Success);
            return host;
        }

        public async Task<PredictStatus> StoredStatus()
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            return await db.PredictStatuses.AsNoTracking().SingleAsync(status => status.MonitorPingInfoID == HostId);
        }

        public async Task AssertSavedAndPublished(bool alert, DetectionResult change, DetectionResult spike)
        {
            var stored = await StoredStatus();
            Assert.Equal(alert, stored.AlertFlag);
            Assert.Equal(alert ? 1 : 0, stored.DownCount);
            Assert.Equal(change.IsIssueDetected, stored.ChangeDetectionResult.IsIssueDetected);
            Assert.Equal(spike.IsIssueDetected, stored.SpikeDetectionResult.IsIssueDetected);
            Assert.Equal(change.NumberOfDetections, stored.ChangeDetectionResult.NumberOfDetections);
            Assert.Equal(spike.NumberOfDetections, stored.SpikeDetectionResult.NumberOfDetections);
            Assert.Equal(change.IndexOfFirstDetection, stored.ChangeDetectionResult.IndexOfFirstDetection);
            Assert.Equal(spike.IndexOfFirstDetection, stored.SpikeDetectionResult.IndexOfFirstDetection);
            var message = Published.Last();
            Assert.Equal("fixture-predict", message.AppID);
            var published = Assert.Single(message.PredictStatusAlerts);
            Assert.Equal(HostId, published.ID);
            Assert.Equal("fixture-user", published.UserID);
            Assert.Equal("fixture.example", published.Address);
            Assert.Equal(alert, published.AlertFlag);
            Assert.Equal(stored.AlertSent, published.AlertSent);
            Assert.Equal(stored.EventTime, published.EventTime);
            Assert.Equal(change.NumberOfDetections, published.ChangeDetectionResult.NumberOfDetections);
            Assert.Equal(spike.NumberOfDetections, published.SpikeDetectionResult.NumberOfDetections);
        }

        public async Task Configure(Action<MonitorModelConfig> configure)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var host = await db.MonitorIPs.Include(ip => ip.ModelConfig).SingleAsync(ip => ip.ID == HostId);
            host.ModelConfig ??= new MonitorModelConfig();
            configure(host.ModelConfig);
            await db.SaveChangesAsync();
        }

        public async Task RemoveConfig()
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var host = await db.MonitorIPs.SingleAsync(ip => ip.ID == HostId);
            host.ModelConfig = null;
            host.MonitorModelConfigId = null;
            await db.SaveChangesAsync();
        }

        public async Task Append(int[] readings)
        {
            var update = Host(readings, _nextSample);
            _nextSample += readings.Length;
            using (var scope = _provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MonitorContext>();
                var host = await db.MonitorPingInfos.Include(h => h.PingInfos).SingleAsync(h => h.ID == HostId);
                host.PingInfos.AddRange(update.PingInfos);
                host.DateEnded = update.DateEnded;
                await db.SaveChangesAsync();
            }
            Assert.True(Repository.UpdateMonitorPingInfo(update).Success);
        }

        public async Task DeleteLiveHost()
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            db.MonitorPingInfos.Remove(await db.MonitorPingInfos.SingleAsync(host => host.ID == HostId));
            await db.SaveChangesAsync();
        }

        public async Task AddHistorical(int[] readings)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var historical = Host(readings, -readings.Length);
            historical.ID = 0;
            historical.DataSetID = 1;
            db.MonitorPingInfos.Add(historical);
            await db.SaveChangesAsync();
        }

        public void Dispose()
        {
            Forecast.Dispose();
            _provider.Dispose();
            foreach (var filename in new[] { $"model_{HostId}.zip", $"spike_model_{HostId}.zip" })
            {
                var path = Path.Combine("data", filename);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
