using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor.ML.Services;

internal sealed class AdaptiveWindowManager
{
    internal sealed record AdaptiveWindowSettings(
        int MaxChangeWindow,
        int MaxSpikeWindow,
        int MinChangeWindow,
        int MinSpikeWindow,
        int ChangeStep,
        int SpikeStep,
        int ChangePreTrainMax,
        int SpikePreTrainMax,
        int ChangePreTrainMin,
        int SpikePreTrainMin,
        int ChangePreTrainStep,
        int SpikePreTrainStep,
        double GrowMartingale,
        double ShrinkMartingale,
        double GrowBoostThreshold,
        double ShrinkBoostThreshold,
        TimeSpan AlertCooldown);

    internal readonly record struct DetectionSnapshot(bool IsIssueDetected, int NumberOfDetections, double MaxMartingaleValue);

    internal readonly record struct WindowConfig(int ChangeWindow, int ChangePreTrain, int SpikeWindow, int SpikePreTrain, int MaxChangeWindow, int MaxSpikeWindow);

    private sealed class WindowState
    {
        public int ChangeWindow;
        public int SpikeWindow;
        public int ChangePreTrain;
        public int SpikePreTrain;
        public DateTime? ChangeLastAlertUtc;
        public DateTime? SpikeLastAlertUtc;
        public readonly object SyncRoot = new();
    }

    private AdaptiveWindowSettings _settings;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, WindowState> _states = new();

    public AdaptiveWindowManager(AdaptiveWindowSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public void ApplySettings(AdaptiveWindowSettings settings)
    {
        _settings = settings;
        foreach (var state in _states.Values)
        {
            lock (state.SyncRoot)
            {
                state.ChangeWindow = Math.Clamp(state.ChangeWindow, _settings.MinChangeWindow, _settings.MaxChangeWindow);
                state.SpikeWindow = Math.Clamp(state.SpikeWindow, _settings.MinSpikeWindow, _settings.MaxSpikeWindow);

                state.ChangePreTrain = Math.Clamp(state.ChangePreTrain, _settings.ChangePreTrainMin, Math.Min(_settings.ChangePreTrainMax, state.ChangeWindow - 1));
                state.SpikePreTrain = Math.Clamp(state.SpikePreTrain, _settings.SpikePreTrainMin, Math.Min(_settings.SpikePreTrainMax, state.SpikeWindow - 1));
            }
        }
    }

    public WindowConfig GetConfig(int monitorId)
    {
        var state = GetOrAddState(monitorId);
        lock (state.SyncRoot)
        {
            return new WindowConfig(state.ChangeWindow, state.ChangePreTrain, state.SpikeWindow, state.SpikePreTrain, _settings.MaxChangeWindow, _settings.MaxSpikeWindow);
        }
    }

    public WindowConfig Update(int monitorId, DetectionSnapshot change, DetectionSnapshot spike, DateTime observedAtUtc)
    {
        var state = GetOrAddState(monitorId);
        lock (state.SyncRoot)
        {
            UpdateForMode(
                state,
                monitorId,
                isChange: true,
                snapshot: change,
                ref state.ChangeWindow,
                ref state.ChangePreTrain,
                ref state.ChangeLastAlertUtc,
                observedAtUtc);

            UpdateForMode(
                state,
                monitorId,
                isChange: false,
                snapshot: spike,
                ref state.SpikeWindow,
                ref state.SpikePreTrain,
                ref state.SpikeLastAlertUtc,
                observedAtUtc);

            return new WindowConfig(state.ChangeWindow, state.ChangePreTrain, state.SpikeWindow, state.SpikePreTrain, _settings.MaxChangeWindow, _settings.MaxSpikeWindow);
        }
    }

    private WindowState GetOrAddState(int monitorId)
    {
        return _states.GetOrAdd(monitorId, _ =>
        {
            int changeStart = Math.Clamp(75, _settings.MinChangeWindow, _settings.MaxChangeWindow);
            int spikeStart = Math.Clamp(70, _settings.MinSpikeWindow, _settings.MaxSpikeWindow);
            var state = new WindowState
            {
                ChangeWindow = changeStart,
                SpikeWindow = spikeStart,
                ChangePreTrain = Math.Min(_settings.ChangePreTrainMax, Math.Max(changeStart - 1, _settings.ChangePreTrainMin)),
                SpikePreTrain = Math.Min(_settings.SpikePreTrainMax, Math.Max(spikeStart - 1, _settings.SpikePreTrainMin))
            };
            state.ChangePreTrain = Math.Max(_settings.ChangePreTrainMin, Math.Min(state.ChangePreTrain, state.ChangeWindow - 1));
            state.SpikePreTrain = Math.Max(_settings.SpikePreTrainMin, Math.Min(state.SpikePreTrain, state.SpikeWindow - 1));
            return state;
        });
    }

    private void UpdateForMode(
        WindowState state,
        int monitorId,
        bool isChange,
        DetectionSnapshot snapshot,
        ref int window,
        ref int preTrain,
        ref DateTime? lastAlertUtc,
        DateTime observedAtUtc)
    {
        var settings = _settings;
        bool grow = snapshot.MaxMartingaleValue >= settings.GrowMartingale;
        bool shrink = snapshot.MaxMartingaleValue <= settings.ShrinkMartingale;

        if (grow)
        {
            lastAlertUtc = observedAtUtc;

            var maxWindow = isChange ? settings.MaxChangeWindow : settings.MaxSpikeWindow;
            var step = isChange ? settings.ChangeStep : settings.SpikeStep;
            if (snapshot.MaxMartingaleValue >= settings.GrowMartingale + settings.GrowBoostThreshold)
                step *= 2;
            if (window < maxWindow)
            {
                int newWindow = Math.Min(maxWindow, window + step);
                if (newWindow != window)
                {
                    _logger.LogInformation("Adaptive windows: monitor={Monitor} {Mode} window increased to {Window}", monitorId, isChange ? "change" : "spike", newWindow);
                    window = newWindow;
                }
            }

            var preTrainMax = isChange ? settings.ChangePreTrainMax : settings.SpikePreTrainMax;
            var preTrainStep = isChange ? settings.ChangePreTrainStep : settings.SpikePreTrainStep;
            preTrain = Math.Min(preTrainMax, preTrain + preTrainStep);
            preTrain = Math.Min(preTrain, window - 1);
            return;
        }

        if (!shrink)
        {
            return;
        }
        var minWindow = isChange ? settings.MinChangeWindow : settings.MinSpikeWindow;
        var stepDown = isChange ? settings.ChangeStep : settings.SpikeStep;
        if (snapshot.MaxMartingaleValue <= settings.ShrinkMartingale - settings.ShrinkBoostThreshold)
            stepDown *= 2;
        if (window <= minWindow)
            return;

        int newWindowDown = Math.Max(minWindow, window - stepDown);
        if (newWindowDown != window)
        {
            _logger.LogInformation("Adaptive windows: monitor={Monitor} {Mode} window reduced to {Window}", monitorId, isChange ? "change" : "spike", newWindowDown);
            window = newWindowDown;
        }

        var preTrainMin = isChange ? settings.ChangePreTrainMin : settings.SpikePreTrainMin;
        var preTrainStepDown = isChange ? settings.ChangePreTrainStep : settings.SpikePreTrainStep;
        preTrain = Math.Max(preTrainMin, preTrain - preTrainStepDown);
        preTrain = Math.Min(preTrain, window - 1);
    }
}
