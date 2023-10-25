
using Microsoft.ML;
using System.Collections.Generic;
using System;
namespace NetworkMonitor.ML.Model;

public abstract class MLModel : IMLModel
{
  private int _monitorPingInfoID;

  public int MonitorPingInfoID { get => _monitorPingInfoID; }

  public MLModel(int monitorPingInfoID)
  {
    _monitorPingInfoID = monitorPingInfoID;
  }

  public abstract void Train(List<LocalPingInfo> data);
  public abstract float Predict(LocalPingInfo input);
  public abstract IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs);
  public virtual void PrintPrediction(IEnumerable<AnomalyPrediction> predictions)
  {

    Console.WriteLine("Alert\tScore\tP-Value");
    foreach (var p in predictions)
    {
      if (p.Prediction is not null)
      {
        var results = $"{p.Prediction[0]}\t{p.Prediction[1]:f2}\t{p.Prediction[2]:F2}";

        if (p.Prediction[0] == 1)
        {
          results += " <-- alert is on, predicted changepoint";
          //anomalyDetected=true;
        }
        Console.WriteLine(results);
      }
    }
  }

}
