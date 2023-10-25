
using Microsoft.ML.Data;
using Newtonsoft.Json;
namespace NetworkMonitor.ML.Model;

public class LocalPingInfo
{
    public uint DateSentInt { get; set; } // Assuming DateSent is represented as a float for simplicity. You might need to preprocess DateTime to a float representation.
    public float RoundTripTime { get; set; }
    public ushort StatusID {get;set;}
}

 public class AnomalyPrediction
        {
            [VectorType(4)]
            public double[] Prediction { get; set; }
        }
