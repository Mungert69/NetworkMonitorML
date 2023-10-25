using System.Collections.Generic;


namespace NetworkMonitor.ML.Model;
public interface IMLModel
{
    void Train(List<LocalPingInfo> data);
    float Predict(LocalPingInfo input);
    IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs);
    void PrintPrediction(IEnumerable<AnomalyPrediction> predictions);
}
