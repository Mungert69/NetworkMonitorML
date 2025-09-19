// tests/TimesFmRabbitModelTests.cs
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using NetworkMonitor.ML.Model;
using NetworkMonitor.Objects;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

using static NetworkMonitorML.IntegrationTests.TestHelpers;

namespace NetworkMonitorML.IntegrationTests;

public sealed class TimesFmRabbitModelTests
{
    private static IEnumerable<LocalPingInfo> MakePings(params double[] rtts)
        => rtts.Select((v, i) => new LocalPingInfo { DateSentInt = (uint)(i + 1), RoundTripTime = (float)v, StatusID = 0 });

    [Fact(DisplayName = "Happy path: forecasts and quantiles present"), Trait("Category","Integration")]
    public async Task HappyPath_WithQuantiles()
    {
        var sys = LocalRabbitUrl();

        await using var responder = new FakeSpaceResponder(sys, (payload, replyKey) =>
        {
            int k = 1;
            try
            {
                if (payload.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    var user = msgs[1].GetProperty("content").GetString() ?? "{}";
                    using var inner = JsonDocument.Parse(user);
                    var series = inner.RootElement.GetProperty("series");
                    if (series.ValueKind == JsonValueKind.Array) k = series.GetArrayLength();
                }
            }
            catch { k = 1; }

            var forecasts = Enumerable.Repeat(new[] { 101.0 }, k).ToArray(); // [[101],[101],...]
            var qrow = new[] { 100.0, 95.0, 96.0, 97.0, 98.0, 101.0, 102.0, 103.0, 104.0, 105.0 }; // mean,q10..q90
            var quantiles = new[] { qrow };

            var content = JsonSerializer.Serialize(new
            {
                model = "google/timesfm-2.5-200m-pytorch",
                horizon = 1,
                forecast = forecasts,
                quantiles,
                backend = "timesfm-2.5"
            });
            return new[] { content };
        });

        var repo = MakeRabbitRepo(sys);
        try
        {
            var log = NullLogger<TimesFmRabbitModel>.Instance;
            var model = new TimesFmRabbitModel(repo, sys, log, monitorPingInfoID: 42, confidence: 0.8, preTrain: 2, routingKey: "");

            var window = MakePings(90, 95, 100, 101, 102).ToList();
            var preds = model.PredictList(window).ToList();

            Assert.Equal(window.Count, preds.Count);
            Assert.Equal(0, preds[0].Prediction[0]);
            Assert.Equal(0.5, preds[1].Prediction[2], 3);

            Assert.Equal(0, preds[2].Prediction[0]);
            Assert.True(preds[2].Prediction[1] > 0);
            Assert.Equal(0, preds[3].Prediction[0]);
            Assert.Equal(0, preds[4].Prediction[0]);
        }
        finally
        {
            await repo.ShutdownRepo();
        }
    }

    [Fact(DisplayName = "Quantiles absent -> neutral bands"), Trait("Category","Integration")]
    public async Task MissingQuantiles()
    {
        var sys = LocalRabbitUrl();

        await using var responder = new FakeSpaceResponder(sys, (payload, replyKey) =>
        {
            var content = JsonSerializer.Serialize(new
            {
                model = "google/timesfm-2.5-200m-pytorch",
                horizon = 1,
                forecast = new[] { 50.0 },
                quantiles = (object?)null,
                backend = "timesfm-2.5"
            });
            return new[] { content };
        });

        var repo = MakeRabbitRepo(sys);
        try
        {
            var log = NullLogger<TimesFmRabbitModel>.Instance;
            var model = new TimesFmRabbitModel(repo, sys, log, monitorPingInfoID: 7, confidence: 0.8, preTrain: 1, routingKey: "");

            var window = MakePings(49, 50, 51).ToList();
            var preds = model.PredictList(window).ToList();

            Assert.Equal(window.Count, preds.Count);
            Assert.Equal(0, preds[0].Prediction[0]);
            Assert.Equal(0, preds[1].Prediction[0]);
            Assert.Equal(0, preds[2].Prediction[0]);
            Assert.Equal(0.5, preds[1].Prediction[2], 3);
        }
        finally
        {
            await repo.ShutdownRepo();
        }
    }

    [Fact(DisplayName = "Shape variants parse"), Trait("Category","Integration")]
    public async Task WrappedVsUnwrappedShapes()
    {
        var sys = LocalRabbitUrl();

        int call = 0;
        await using var responder = new FakeSpaceResponder(sys, (payload, replyKey) =>
        {
            call++;
            if (call == 1)
            {
                var content = JsonSerializer.Serialize(new
                {
                    model = "google/timesfm-2.5-200m-pytorch",
                    horizon = 1,
                    forecast = new[] { new[] { 10.0 } }, // [[v]]
                    quantiles = new[] { new[] { 9.0, 8.0, 8.5, 9.0, 9.5, 10.0, 10.5, 11.0, 11.5, 12.0 } },
                    backend = "timesfm-2.5"
                });
                return new[] { content };
            }
            else
            {
                var content = JsonSerializer.Serialize(new
                {
                    model = "google/timesfm-2.5-200m-pytorch",
                    horizon = 1,
                    forecast = new[] { 10.0 },          // [v]
                    quantiles = new[] { 9.0, 8.0, 8.5, 9.0, 9.5, 10.0, 10.5, 11.0, 11.5, 12.0 },
                    backend = "timesfm-2.5"
                });
                return new[] { content };
            }
        });

        var repo1 = MakeRabbitRepo(sys);
        try
        {
            var log = NullLogger<TimesFmRabbitModel>.Instance;

            var model1 = new TimesFmRabbitModel(repo1, sys, log, monitorPingInfoID: 1, confidence: 0.8, preTrain: 1, routingKey: "");
            var preds1 = model1.PredictList(MakePings(9.7, 10.2).ToList()).ToList();

            var repo2 = MakeRabbitRepo(sys);
            try
            {
                var model2 = new TimesFmRabbitModel(repo2, sys, log, monitorPingInfoID: 2, confidence: 0.8, preTrain: 1, routingKey: "");
                var preds2 = model2.PredictList(MakePings(9.7, 10.2).ToList()).ToList();

                Assert.Equal(preds1.Select(p => p.Prediction[0]), preds2.Select(p => p.Prediction[0]));
            }
            finally
            {
                await repo2.ShutdownRepo();
            }
        }
        finally
        {
            await repo1.ShutdownRepo();
        }
    }
}
