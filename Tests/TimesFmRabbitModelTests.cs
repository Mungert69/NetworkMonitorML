using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkMonitor.ML.Model;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using Xunit;

namespace NetworkMonitorML.IntegrationTests;

public sealed class TimesFmRabbitModelTests
{
    private static List<LocalPingInfo> Pings(params double[] values) =>
        values.Select((value, index) => new LocalPingInfo
        {
            DateSentInt = (uint)(index + 1),
            RoundTripTime = (float)value,
            StatusID = 0
        }).ToList();

    private static TimesFmRabbitModel Model(
        Func<int, string> reply,
        int preTrain = 2,
        TimesFmResolvedSettings? settings = null)
    {
        Task<string> Respond(string request, CancellationToken _)
        {
            using var envelope = JsonDocument.Parse(request);
            var content = envelope.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var payload = JsonDocument.Parse(content);
            return Task.FromResult(reply(payload.RootElement.GetProperty("series").GetArrayLength()));
        }

        return new TimesFmRabbitModel(
            new Mock<IRabbitRepo>().Object,
            new SystemUrl(),
            NullLogger<TimesFmRabbitModel>.Instance,
            monitorPingInfoID: 1,
            confidence: 0.8,
            preTrain: preTrain,
            modelType: "Change",
            routingKey: "",
            settings: settings,
            responseReader: Respond);
    }

    private static string Reply(int count, double forecast = 100, double[]? quantiles = null)
    {
        quantiles ??= new[] { 100d, 90, 92, 94, 96, 104, 106, 108, 110, 112 };
        return JsonSerializer.Serialize(new
        {
            horizon = 1,
            forecast = Enumerable.Range(0, count).Select(_ => new[] { forecast }).ToArray(),
            quantiles = Enumerable.Range(0, count).Select(_ => quantiles).ToArray()
        });
    }

    [Fact]
    public void ForecastsAndQuantiles_ProduceAnAlignedPredictionForEveryReading()
    {
        using var model = Model(count => Reply(count));
        var inputs = Pings(90, 95, 100, 101, 102);

        var predictions = model.PredictList(inputs).ToList();

        Assert.Equal(inputs.Count, predictions.Count);
        Assert.All(predictions, prediction => Assert.Equal(4, prediction.Prediction.Length));
        Assert.All(predictions.Take(2), prediction => Assert.Equal(0, prediction.Prediction[0]));
        Assert.True(predictions[3].Prediction[1] > 0);
    }

    [Fact]
    public void MissingQuantiles_UsesRobustFallbackBands()
    {
        using var model = Model(count => JsonSerializer.Serialize(new
        {
            horizon = 1,
            forecast = Enumerable.Repeat(new[] { 50d }, count).ToArray(),
            quantiles = (object?)null
        }), preTrain: 1);

        var predictions = model.PredictList(Pings(49, 50, 51)).ToList();

        Assert.Equal(3, predictions.Count);
        Assert.All(predictions, prediction => Assert.Equal(0, prediction.Prediction[0]));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("per-prefix")]
    [InlineData("nested")]
    public void SupportedForecastShapes_AreNormalized(string shape)
    {
        using var model = Model(count =>
        {
            object forecast = shape switch
            {
                "flat" => new[] { 100d },
                "per-prefix" => Enumerable.Repeat(100d, count).ToArray(),
                _ => Enumerable.Range(0, count).Select(_ => new[] { 100d }).ToArray()
            };
            return JsonSerializer.Serialize(new { horizon = 1, forecast, quantiles = (object?)null });
        });

        var predictions = model.PredictList(Pings(100, 100, 100, 100)).ToList();

        Assert.Equal(4, predictions.Count);
        Assert.All(predictions, prediction => Assert.Equal(0, prediction.Prediction[0]));
    }

    [Fact]
    public void PersistentLargeObservedLatencySpike_RaisesAnAlert()
    {
        var settings = new TimesFmResolvedSettings
        {
            RunLength = 2, KOfNK = 2, KOfNN = 2,
            MinRelShift = 0.1, MinBandAbs = 1, MinBandRel = 0, MadAlpha = 0, LogJson = false
        };
        using var model = Model(count => Reply(count), settings: settings);

        var predictions = model.PredictList(Pings(100, 100, 100, 140, 145)).ToList();

        Assert.Equal(0, predictions[2].Prediction[0]);
        Assert.Equal(1, predictions[4].Prediction[0]);
    }

    [Fact]
    public void CalmReadings_DoNotRaiseAlerts()
    {
        using var model = Model(count => Reply(count));

        var predictions = model.PredictList(Pings(100, 101, 99, 100, 101, 100)).ToList();

        Assert.All(predictions, prediction => Assert.Equal(0, prediction.Prediction[0]));
    }

    [Fact]
    public void ShortHistory_UsesTheAvailablePrefixInsteadOfFailing()
    {
        using var model = Model(count => Reply(count), preTrain: 20);

        var predictions = model.PredictList(Pings(100, 110)).ToList();

        Assert.Equal(2, predictions.Count);
        Assert.Equal(0, predictions[0].Prediction[0]);
    }

    [Fact]
    public void InvalidForecastShape_IsRejected()
    {
        using var model = Model(_ => "{\"horizon\":1,\"forecast\":{\"invalid\":true},\"quantiles\":null}");

        Assert.Throws<InvalidOperationException>(() => model.PredictList(Pings(1, 2, 3)).ToList());
    }

    [Fact]
    public void SigmaCooldown_DecrementsAcrossSubsequentEvaluations()
    {
        var settings = new TimesFmResolvedSettings
        {
            RunLength = 1, KOfNK = 1, KOfNN = 1, SigmaCooldown = 3,
            MinRelShift = 0.01, MinBandAbs = 1, MinBandRel = 0, MadAlpha = 0, LogJson = false
        };
        using var model = Model(count => Reply(count), settings: settings);

        model.PredictList(Pings(100, 100, 150)).ToList();
        model.PredictList(Pings(100, 100, 100)).ToList();

        Assert.Equal(2, model.CooldownRemaining);
    }
}
