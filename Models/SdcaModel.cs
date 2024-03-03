
using Microsoft.ML;
using System.Collections.Generic;
namespace NetworkMonitor.ML.Model;

public class SdcaModel : MLModel
{

    private Trainer _trainer;
    private Predictor _predictor;
    private MLContext _mlContext;

    public SdcaModel(int monitorPingInfoID) : base(monitorPingInfoID)
    {
        var modelPath = $"model_{monitorPingInfoID}.zip";
        _mlContext=new MLContext();
        _trainer = new Trainer(modelPath, _mlContext);
        _predictor = new Predictor(modelPath, _mlContext);
    }

    public override void Train(List<LocalPingInfo> data)
    {
        _trainer.Train(data);
    }

    public override float Predict(LocalPingInfo input)
    {
        return _predictor.Predict(input);
    }

 public override IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs)
    {
        //TODO implement this
        return new List<AnomalyPrediction>();
    }

     public class Trainer
    {
        private readonly string _modelPath;
        private MLContext _mlContext;

        public Trainer(string modelPath, MLContext mlContext)
        {
            _modelPath = modelPath;
            _mlContext = mlContext;
        }

        public void Train(List<LocalPingInfo> localPingInfos)
        {

            // Load data into ML.NET data view
            var data = _mlContext.Data.LoadFromEnumerable(localPingInfos);

       var pipeline = _mlContext.Transforms.Concatenate("Features",nameof(LocalPingInfo.RoundTripTime))
    .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
    .Append(_mlContext.Regression.Trainers.Sdca(labelColumnName: nameof(LocalPingInfo.RoundTripTime), maximumNumberOfIterations: 100));

            var model = pipeline.Fit(data);

            _mlContext.Model.Save(model, data.Schema, _modelPath);
        }
    }


public class Predictor
{
    private readonly string _modelPath;
    private MLContext _mlContext;
    private ITransformer _model;
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    public Predictor(string modelPath, MLContext mlContext)
    {
        _modelPath = modelPath;
        _mlContext = mlContext;

    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.


    public float Predict(LocalPingInfo input)
    {
         _model = _mlContext.Model.Load(_modelPath, out var modelSchema);
        var predictionEngine = _mlContext.Model.CreatePredictionEngine<LocalPingInfo, AnomalyPrediction>(_model);
        var prediction = predictionEngine.Predict(input);
        return 0f;
    }
}


}

