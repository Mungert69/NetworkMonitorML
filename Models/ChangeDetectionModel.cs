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
        private Trainer _trainer;
        private Predictor _predictor;
        private MLContext _mlContext;

        public ChangeDetectionModel(int monitorPingInfoID) : base(monitorPingInfoID)
        {
            var modelPath = $"model_{monitorPingInfoID}.zip";
            _mlContext = new MLContext();
            _trainer = new Trainer(modelPath, _mlContext);
            _predictor = new Predictor(modelPath, _mlContext);
        }

        public override void PrintPrediction(IEnumerable<AnomalyPrediction> predictions)
        {

            Console.WriteLine("Alert\tScore\tP-Value\tMartingale value");
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
                    Console.WriteLine(results);
                }
            }
            Console.WriteLine("");

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
            private TimeSeriesPredictionEngine<LocalPingInfo, AnomalyPrediction> _engine;

            public Trainer(string modelPath, MLContext mLContext)
            {
                _modelPath = modelPath;
                _mlContext = mLContext;
            }

            public TimeSeriesPredictionEngine<LocalPingInfo, AnomalyPrediction> Engine { get => _engine; set => _engine = value; }

            public void Train(List<LocalPingInfo> localPingInfos)
            {

                var dataView = _mlContext.Data.LoadFromEnumerable(new List<LocalPingInfo>());
                string outputColumnName = nameof(AnomalyPrediction.Prediction);
                string inputColumnName = nameof(LocalPingInfo.RoundTripTime);


                /*var outputDataView = _mlContext.AnomalyDetection.DetectEntireAnomalyBySrCnn(dataView, outputColumnName, inputColumnName,
                    threshold: 0.35, batchSize: 100, sensitivity: 90.0, detectMode: SrCnnDetectMode.AnomalyAndMargin);

                // Getting the data of the newly created column as an IEnumerable of
                // SrCnnAnomalyDetection.
                var predictionColumn = _mlContext.Data.CreateEnumerable<AnomalyPrediction>(
                    outputDataView, reuseRowObject: false);


                Console.WriteLine("Index\tData\tAnomaly\tAnomalyScore\tMag\tExpectedValue\tBoundaryUnit\tUpperBoundary\tLowerBoundary");

                int k = 0;
                foreach (var prediction in predictionColumn)
                {
                    Display.PrintPrediction(k, localPingInfos[k].RoundTripTime, prediction);
                    k++;
                }

                ITransformer model = _mlContext.Transforms.DetectAnomalyBySrCnn(outputColumnName, inputColumnName,
                               threshold: 0.35, averagingWindowSize : 10).Fit(
                               dataView);*/
                ITransformer model = _mlContext.Transforms.DetectIidChangePoint(outputColumnName, inputColumnName, confidence: 95d, changeHistoryLength: 50).Fit(
                                               dataView); ;
                // Create a time series prediction engine from the model.
                Engine = model.CreateTimeSeriesEngine<LocalPingInfo,
                    AnomalyPrediction>(_mlContext);


                Engine.CheckPoint(_mlContext, _modelPath);

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

                var iidChangePointEstimator = _mlContext.Transforms.DetectIidChangePoint(outputColumnName, inputColumnName, confidence: 90d, 20);

                var emptyDataView = _mlContext.Data.LoadFromEnumerable(new List<LocalPingInfo>());
                var iidChangePointTransform = iidChangePointEstimator.Fit(emptyDataView);

                var dataView = _mlContext.Data.LoadFromEnumerable(inputs);
                IDataView transformedData = iidChangePointTransform.Transform(dataView);
                var predictions = _mlContext.Data.CreateEnumerable<AnomalyPrediction>(transformedData, reuseRowObject: false);


                return predictions;
            }

            public float GetDeviation(LocalPingInfo input)
            {
                // Load the model.
                var file = File.OpenRead(_modelPath);
                var model = _mlContext.Model.Load(file, out DataViewSchema schema);
                var engine = model.CreateTimeSeriesEngine<LocalPingInfo,
                   AnomalyPrediction>(_mlContext);
                var prediction = engine.Predict(input);
                return (float)prediction.Prediction[1]; // Return the anomaly score
            }
        }


    }


}
