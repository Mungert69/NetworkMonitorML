using System;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkMonitor.ML.Services;
using Xunit;

namespace NetworkMonitorML.Tests;

public class AdaptiveWindowManagerTests
{
    private static AdaptiveWindowManager CreateManager(out AdaptiveWindowManager.AdaptiveWindowSettings settings)
    {
        settings = new AdaptiveWindowManager.AdaptiveWindowSettings(
            MaxChangeWindow: 60,
            MaxSpikeWindow: 40,
            MinChangeWindow: 30,
            MinSpikeWindow: 20,
            ChangeStep: 10,
            SpikeStep: 5,
            ChangePreTrainMax: 20,
            SpikePreTrainMax: 15,
            ChangePreTrainMin: 10,
            SpikePreTrainMin: 5,
            ChangePreTrainStep: 5,
            SpikePreTrainStep: 5,
            GrowMartingale: 1.05,
            ShrinkMartingale: 1.03,
            GrowBoostThreshold: 0.05,
            ShrinkBoostThreshold: 0.03,
            AlertCooldown: TimeSpan.FromMinutes(5));
        return new AdaptiveWindowManager(settings, NullLogger.Instance);
    }

    [Fact]
    public void QuietRunsShrinkWindows()
    {
        var manager = CreateManager(out var settings);
        var initial = manager.GetConfig(42);
        var first = manager.Update(42, QuietSnapshot(settings), QuietSnapshot(settings), DateTime.UtcNow);
        var second = manager.Update(42, QuietSnapshot(settings), QuietSnapshot(settings), DateTime.UtcNow.AddMinutes(1));

        Assert.True(first.ChangeWindow < initial.ChangeWindow);
        Assert.True(second.ChangeWindow <= first.ChangeWindow);
        Assert.True(second.ChangeWindow >= settings.MinChangeWindow);

        Assert.True(first.SpikeWindow < initial.SpikeWindow);
        Assert.True(second.SpikeWindow <= first.SpikeWindow);
        Assert.True(second.SpikeWindow >= settings.MinSpikeWindow);
    }

    [Fact]
    public void HotRunRestoresWindows()
    {
        var manager = CreateManager(out var settings);
        var first = manager.Update(7, QuietSnapshot(settings), QuietSnapshot(settings), DateTime.UtcNow);
        var shrunken = manager.Update(7, QuietSnapshot(settings), QuietSnapshot(settings), DateTime.UtcNow.AddMinutes(1));

        // Hot run should bring both windows back up.
        var hot = new AdaptiveWindowManager.DetectionSnapshot(true, 1, settings.GrowMartingale);
        var restored = manager.Update(7, hot, hot, DateTime.UtcNow.AddMinutes(2));

        Assert.True(shrunken.ChangeWindow < first.ChangeWindow);
        Assert.True(restored.ChangeWindow > shrunken.ChangeWindow);

        Assert.True(shrunken.SpikeWindow < first.SpikeWindow);
        Assert.True(restored.SpikeWindow > shrunken.SpikeWindow);
    }

    private static AdaptiveWindowManager.DetectionSnapshot QuietSnapshot(AdaptiveWindowManager.AdaptiveWindowSettings settings)
        => new(false, 0, settings.ShrinkMartingale - 0.01);
}
