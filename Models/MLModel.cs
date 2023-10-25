
using Microsoft.ML;
using System.Collections.Generic;
namespace NetworkMonitor.ML.Model;

public abstract class MLModel : IMLModel
{
    private int _monitorPingInfoID;

    public int MonitorPingInfoID { get => _monitorPingInfoID;  }

    public MLModel(int monitorPingInfoID)
    {
      _monitorPingInfoID=monitorPingInfoID;
    }

    public abstract void Train(List<LocalPingInfo> data);
    public abstract float Predict(LocalPingInfo input);
    public abstract IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs);

}
