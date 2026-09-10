using System;
using System.Collections.Generic;

namespace NetworkMonitor.ML.Services;

internal static class PredictionWindow
{
    // Bound retention during long outages; insufficient usable history must
    // remain a data-limited result rather than reaching arbitrarily far back.
    internal static int Capacity(int window) => (int)Math.Min(int.MaxValue, 4L * Math.Max(0, window));

    internal static List<T> Trim<T>(List<T> samples, int window, Func<T, bool> isUsable)
    {
        if (window <= 0) return samples;
        int start = samples.Count;
        int usable = 0;
        int oldest = Math.Max(0, samples.Count - Capacity(window));
        while (start > oldest && usable < window)
        {
            start--;
            if (isUsable(samples[start])) usable++;
        }
        return start == 0 ? samples : samples.GetRange(start, samples.Count - start);
    }
}
