using System;
using System.Collections.Generic;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.TimeSeries;
using Microsoft.ML.Transforms.TimeSeries;
using System.IO;
using NetworkMonitor.Objects;

namespace NetworkMonitor.ML.Model
{
    public class SpikeDetectionModel : MLModel
    {
        private Trainer _trainer;
        private Predictor _predictor;
        private MLContext _mlContext;

        public SpikeDetectionModel(int monitorPingInfoID) : base(monitorPingInfoID)
        {
            var modelPath = $"spike_model_{monitorPingInfoID}.zip";
            _mlContext = new MLContext();
            _trainer = new Trainer(modelPath, _mlContext);
            _predictor = new Predictor(modelPath, _mlContext);
        }

        public override void Train(List<LocalPingInfo> data)
        {
            _trainer.Train(data);
        }

        public override float Predict(LocalPingInfo input)
        {
            return _predictor.GetDeviation(input);
        }

        public override IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs)
        {
            return _predictor.GetDeviations(inputs);
        }

        public class Trainer
        {
            private readonly string _modelPath;
            private MLContext _mlContext;

            public Trainer(string modelPath, MLContext mLContext)
            {
                _modelPath = modelPath;
                _mlContext = mLContext;
            }

            public void Train(List<LocalPingInfo> localPingInfos)
            {
                var dataView = _mlContext.Data.LoadFromEnumerable(new List<LocalPingInfo>());
                string outputColumnName = nameof(AnomalyPrediction.Prediction);
                string inputColumnName = nameof(LocalPingInfo.RoundTripTime);

                ITransformer model = _mlContext.Transforms.DetectIidSpike(outputColumnName, inputColumnName, confidence: 95, pvalueHistoryLength: 50).Fit(dataView);

                // Save the model to a file.
                _mlContext.Model.Save(model, dataView.Schema, _modelPath);
            }
        }

        public class Predictor
        {
            private readonly string _modelPath;
            private MLContext _mlContext;

            public Predictor(string modelPath, MLContext mlContext)
            {
                _mlContext = mlContext;
                _modelPath = modelPath;
            }

            public IEnumerable<AnomalyPrediction> GetDeviations(IEnumerable<LocalPingInfo> inputs)
            {
                string outputColumnName = nameof(AnomalyPrediction.Prediction);
                string inputColumnName = nameof(LocalPingInfo.RoundTripTime);

                var iidSpikeEstimator = _mlContext.Transforms.DetectIidSpike(outputColumnName, inputColumnName, confidence: 95, pvalueHistoryLength: 50);

                var emptyDataView = _mlContext.Data.LoadFromEnumerable(new List<LocalPingInfo>());
                var iidSpikeTransform = iidSpikeEstimator.Fit(emptyDataView);

                var dataView = _mlContext.Data.LoadFromEnumerable(inputs);
                IDataView transformedData = iidSpikeTransform.Transform(dataView);
                var predictions = _mlContext.Data.CreateEnumerable<AnomalyPrediction>(transformedData, reuseRowObject: false);

                return predictions;
            }

            public float GetDeviation(LocalPingInfo input)
            {
                var file = File.OpenRead(_modelPath);
                var model = _mlContext.Model.Load(file, out DataViewSchema schema);
                var engine = model.CreateTimeSeriesEngine<LocalPingInfo, AnomalyPrediction>(_mlContext);
                var prediction = engine.Predict(input);
                return (float)prediction.Prediction[1]; // Return the spike score
            }
        }

    }
}
