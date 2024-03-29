using System;
using System.Collections.Generic;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.TimeSeries;
using Microsoft.ML.Transforms.TimeSeries;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Math.EC.Multiplier;
using NetworkMonitor.Objects;


namespace NetworkMonitor.ML.Model
{
    public class ChangeDetectionModel : MLModel
    {
        private Trainer? _trainer;
        private Predictor _predictor;
        private MLContext _mlContext;
       // private string _basePath = "data";

        public ChangeDetectionModel(int monitorPingInfoID, double confidence, int preTrain) : base(monitorPingInfoID, "data")
        {
            var modelPath = $"{_basePath}/model_{monitorPingInfoID}.zip";
            _mlContext = new MLContext();
            // No need to train this ML model
            //_trainer = new Trainer(modelPath, _mlContext, confidence);
            _predictor = new Predictor(modelPath, _mlContext, confidence, preTrain);
            this.Confidence = confidence;
            this.PreTrain = preTrain;
        }

        public override void PrintPrediction(IEnumerable<AnomalyPrediction> predictions)
        {

            //Console.WriteLine($"Confidence set at {Confidence}");
            //Console.WriteLine("Alert\tScore\tP-Value\tMartingale value");
            foreach (var p in predictions)
            {
                if (p.Prediction is not null)
                {
                    var results = $"{p.Prediction[0]}\t{p.Prediction[1]:f2}\t{p.Prediction[2]:F2}\t{p.Prediction[3]:F2}";

                    if (p.Prediction[0] == 1)
                    {
                        results += " <-- alert is on, predicted changepoint";
                        //anomalyDetected=true;
                    }
                    //Console.WriteLine(results);
                }
            }
            //Console.WriteLine("");

        }
        public override void Train(List<LocalPingInfo> data)
        {
            // No need to train this ML model
            // _trainer.Train(data);
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
                // Not Implemented : this model does support training

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

                /* Load the model.
                var file = File.OpenRead(_modelPath);
                var model = _mlContext.Model.Load(file, out DataViewSchema schema);
                var engine = model.CreateTimeSeriesEngine<LocalPingInfo,
                   AnomalyPrediction>(_mlContext);
                int k = 0;
                var deviations = new List<float>();
                foreach (var input in inputs)
                {
                    var prediction = engine.Predict(input);
                   
                    Display.PrintPrediction(k, input.RoundTripTime, prediction);
                    k++;
                }*/
                string outputColumnName = nameof(AnomalyPrediction.Prediction);
                string inputColumnName = nameof(LocalPingInfo.RoundTripTime);

                var iidChangePointEstimator = _mlContext.Transforms.DetectIidChangePoint(outputColumnName, inputColumnName, confidence: _confidence, changeHistoryLength: _preTrain);

                //var emptyDataView = _mlContext.Data.LoadFromEnumerable(new List<LocalPingInfo>());
                var dataView = _mlContext.Data.LoadFromEnumerable(inputs);
                var iidChangePointTransform = iidChangePointEstimator.Fit(dataView);
                IDataView transformedData = iidChangePointTransform.Transform(dataView);
                var predictions = _mlContext.Data.CreateEnumerable<AnomalyPrediction>(transformedData, reuseRowObject: false);
                _mlContext.Model.Save(iidChangePointTransform, dataView.Schema, _modelPath);
    
                return predictions;
            }

            public AnomalyPrediction GetDeviation(LocalPingInfo input)
            {
                // Load the model.
                var file = File.OpenRead(_modelPath);
                var model = _mlContext.Model.Load(file, out DataViewSchema schema);
                var engine = model.CreateTimeSeriesEngine<LocalPingInfo,
                   AnomalyPrediction>(_mlContext);
                var prediction = engine.Predict(input);
                return prediction;
            }
        }


    }


}
