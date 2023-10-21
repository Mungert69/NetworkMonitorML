
using Microsoft.ML.Data;
namespace NetworkMonitor.ML;

public class LocalPingInfo
{
    public uint DateSentInt { get; set; } // Assuming DateSent is represented as a float for simplicity. You might need to preprocess DateTime to a float representation.
    public ushort? RoundTripTime { get; set; }
}

public class PingPrediction
{
    [ColumnName("Score")]
    public int PredictedRoundTripTime { get; set; }
}
