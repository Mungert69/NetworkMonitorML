using System.Collections.Generic;


namespace NetworkMonitor.ML.Model;
public interface IMLModel
{
    void Train(List<LocalPingInfo> data);
    float Predict(LocalPingInfo input);
    double Confidence { get ; set; }
    int PreTrain { get; set; }
    IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs);
    void PrintPrediction(IEnumerable<AnomalyPrediction> predictions);
}
