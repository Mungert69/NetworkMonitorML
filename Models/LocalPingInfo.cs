
using Microsoft.ML.Data;
using Newtonsoft.Json;
namespace NetworkMonitor.ML.Model;

public class LocalPingInfo
{
    public uint DateSentInt { get; set; } // Assuming DateSent is represented as a float for simplicity. You might need to preprocess DateTime to a float representation.
    public float RoundTripTime { get; set; }
    public ushort StatusID { get; set; }
}

public class AnomalyPrediction
{
    [VectorType(4)]
    public double[] Prediction { get; set; } = new double[4];

    public static AnomalyPrediction Neutral(double martingale = 1.0)
    {
        return new AnomalyPrediction
        {
            Prediction = new[] { 0d, 0d, 0.5d, martingale }
        };
    }
}

internal static class LocalPingInfoExtensions
{
    public static bool IsTimeout(this LocalPingInfo info)
        => info.RoundTripTime >= ushort.MaxValue;
}
