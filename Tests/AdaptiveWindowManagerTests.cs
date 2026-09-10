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

    [Fact]
    public void ExtremeEvidenceUsesBoostedStepWithoutExceedingBounds()
    {
        var manager = CreateManager(out var settings);
        var initial = manager.GetConfig(9);
        var extreme = new AdaptiveWindowManager.DetectionSnapshot(true, 1,
            settings.GrowMartingale + settings.GrowBoostThreshold);
        var grown = manager.Update(9, extreme, extreme, DateTime.UtcNow);

        Assert.Equal(Math.Min(settings.MaxChangeWindow, initial.ChangeWindow + 2 * settings.ChangeStep), grown.ChangeWindow);
        Assert.Equal(Math.Min(settings.MaxSpikeWindow, initial.SpikeWindow + 2 * settings.SpikeStep), grown.SpikeWindow);
        Assert.InRange(grown.ChangePreTrain, settings.ChangePreTrainMin, settings.ChangePreTrainMax);
        Assert.InRange(grown.SpikePreTrain, settings.SpikePreTrainMin, settings.SpikePreTrainMax);
    }

    [Fact]
    public void ApplyingNarrowerSettingsClampsExistingState()
    {
        var manager = CreateManager(out var settings);
        _ = manager.GetConfig(12);
        var narrowed = settings with
        {
            MaxChangeWindow = 45, MaxSpikeWindow = 30,
            ChangePreTrainMax = 18, SpikePreTrainMax = 12
        };
        manager.ApplySettings(narrowed);
        var config = manager.GetConfig(12);

        Assert.InRange(config.ChangeWindow, narrowed.MinChangeWindow, narrowed.MaxChangeWindow);
        Assert.InRange(config.SpikeWindow, narrowed.MinSpikeWindow, narrowed.MaxSpikeWindow);
        Assert.InRange(config.ChangePreTrain, narrowed.ChangePreTrainMin, narrowed.ChangePreTrainMax);
        Assert.InRange(config.SpikePreTrain, narrowed.SpikePreTrainMin, narrowed.SpikePreTrainMax);
    }

    [Fact]
    public void NeutralEvidenceLeavesWindowUnchanged()
    {
        var manager = CreateManager(out var settings);
        var initial = manager.GetConfig(14);
        var neutral = new AdaptiveWindowManager.DetectionSnapshot(false, 0,
            (settings.GrowMartingale + settings.ShrinkMartingale) / 2);
        var updated = manager.Update(14, neutral, neutral, DateTime.UtcNow);

        Assert.Equal(initial, updated);
    }

    [Fact]
    public void RepeatedQuietRunsNeverDropBelowMinimums()
    {
        var manager = CreateManager(out var settings);
        var quiet = QuietSnapshot(settings);
        var current = manager.GetConfig(15);
        for (var i = 0; i < 20; i++)
            current = manager.Update(15, quiet, quiet, DateTime.UtcNow.AddMinutes(i));

        Assert.Equal(settings.MinChangeWindow, current.ChangeWindow);
        Assert.Equal(settings.MinSpikeWindow, current.SpikeWindow);
        Assert.True(current.ChangePreTrain < current.ChangeWindow);
        Assert.True(current.SpikePreTrain < current.SpikeWindow);
    }

    private static AdaptiveWindowManager.DetectionSnapshot QuietSnapshot(AdaptiveWindowManager.AdaptiveWindowSettings settings)
        => new(false, 0, settings.ShrinkMartingale - 0.01);
}
