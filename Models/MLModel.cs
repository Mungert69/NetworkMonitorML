
using Microsoft.ML;
using System.Collections.Generic;
using System;
namespace NetworkMonitor.ML.Model;

public abstract class MLModel : IMLModel
{
  private int _monitorPingInfoID;
  private double _confidence=95d;
  private int _preTrain;

  public int MonitorPingInfoID { get => _monitorPingInfoID; }
    public double Confidence { get => _confidence; set => _confidence = value; }
    public int PreTrain { get => _preTrain; set => _preTrain = value; }

    public MLModel(int monitorPingInfoID)
  {
    _monitorPingInfoID = monitorPingInfoID;
  }

  public abstract void Train(List<LocalPingInfo> data);
  public abstract AnomalyPrediction Predict(LocalPingInfo input);
  public abstract IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs);
  public virtual void PrintPrediction(IEnumerable<AnomalyPrediction> predictions)
  {
    Console.WriteLine($"Confidence set at {Confidence}");
    Console.WriteLine("Alert\tScore\tP-Value");
    foreach (var p in predictions)
    {
      if (p.Prediction is not null)
      {
        var results = $"{p.Prediction[0]}\t{p.Prediction[1]:f2}\t{p.Prediction[2]:F2}";

        if (p.Prediction[0] == 1)
        {
          results += " <-- alert is on, predicted spike";
          //anomalyDetected=true;
        }
        Console.WriteLine(results);
      }
    }
  }

}
