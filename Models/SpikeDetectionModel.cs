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
        private Trainer? _trainer;
        private Predictor _predictor;
        private MLContext _mlContext;
        private string _basePath = "data";

        public SpikeDetectionModel(int monitorPingInfoID, double confidence, int preTrain) : base(monitorPingInfoID)
        {
            var modelPath = $"{_basePath}/spike_model_{monitorPingInfoID}.zip";
            _mlContext = new MLContext();
            //_trainer = new Trainer(modelPath, _mlContext, confidence);
            _predictor = new Predictor(modelPath, _mlContext, confidence, preTrain);
            this.Confidence = confidence;
            this.PreTrain = preTrain;
        }

        public override void Train(List<LocalPingInfo> data)
        {
            // Not Implemented : this model does support training
        }

        public override AnomalyPrediction Predict(LocalPingInfo input)
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
            private double _confidence;

            public Trainer(string modelPath, MLContext mLContext, double confidence)
            {
                _modelPath = modelPath;
                _mlContext = mLContext;
                _confidence = confidence;
            }

            public void Train(List<LocalPingInfo> localPingInfos)
            {
                // Not Implemented this model does not use training.
                /*
                var dataView = _mlContext.Data.LoadFromEnumerable(new List<LocalPingInfo>());
                string outputColumnName = nameof(AnomalyPrediction.Prediction);
                string inputColumnName = nameof(LocalPingInfo.RoundTripTime);

                ITransformer model = _mlContext.Transforms.DetectIidSpike(outputColumnName, inputColumnName, confidence: _confidence, pvalueHistoryLength: 50).Fit(dataView);

                // Save the model to a file.
                _mlContext.Model.Save(model, dataView.Schema, _modelPath);
                */
            }
        }

        public class Predictor
        {
            private readonly string _modelPath;
            private MLContext _mlContext;

            private double _confidence;
            private int _preTrain;

            public Predictor(string modelPath, MLContext mLContext, double confidence, int preTrain)
            {
                _modelPath = modelPath;
                _mlContext = mLContext;
                _confidence = confidence;
                _preTrain = preTrain;
            }

            public IEnumerable<AnomalyPrediction> GetDeviations(IEnumerable<LocalPingInfo> inputs)
            {
                string outputColumnName = nameof(AnomalyPrediction.Prediction);
                string inputColumnName = nameof(LocalPingInfo.RoundTripTime);

                var iidSpikeEstimator = _mlContext.Transforms.DetectIidSpike(outputColumnName, inputColumnName, confidence: _confidence, pvalueHistoryLength : _preTrain);

                var emptyDataView = _mlContext.Data.LoadFromEnumerable(new List<LocalPingInfo>());
                var iidSpikeTransform = iidSpikeEstimator.Fit(emptyDataView);

                var dataView = _mlContext.Data.LoadFromEnumerable(inputs);
                IDataView transformedData = iidSpikeTransform.Transform(dataView);
                var predictions = _mlContext.Data.CreateEnumerable<AnomalyPrediction>(transformedData, reuseRowObject: false);
                _mlContext.Model.Save(iidSpikeTransform, emptyDataView.Schema, _modelPath);
    
                return predictions;
            }

            public AnomalyPrediction GetDeviation(LocalPingInfo input)
            {
                var file = File.OpenRead(_modelPath);
                var model = _mlContext.Model.Load(file, out DataViewSchema schema);
                var engine = model.CreateTimeSeriesEngine<LocalPingInfo, AnomalyPrediction>(_mlContext);
                var prediction = engine.Predict(input);
                return prediction; // Return the spike score
            }
        }

    }
}
