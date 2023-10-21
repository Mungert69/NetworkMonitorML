using Microsoft.ML;
using NetworkMonitor.Objects;

namespace NetworkMonitor.ML;

public class Predictor
{
    private readonly string _modelPath;
    private MLContext _mlContext;
    private ITransformer _model;

    public Predictor(string modelPath)
    {
        _modelPath = modelPath;
        _mlContext = new MLContext();
        _model = _mlContext.Model.Load(_modelPath, out var modelSchema);
    }

    public float Predict(LocalPingInfo input)
    {
        var predictionEngine = _mlContext.Model.CreatePredictionEngine<LocalPingInfo, PingPrediction>(_model);
        var prediction = predictionEngine.Predict(input);
        return prediction.PredictedRoundTripTime;
    }
}
