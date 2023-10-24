using Microsoft.ML;
using NetworkMonitor.Data;
using System.Collections.Generic;
using System.Linq;

namespace NetworkMonitor.ML
{
    public class Trainer
    {
        private readonly string _modelPath;
        private MLContext _mlContext;

        public Trainer(string modelPath)
        {
            _modelPath = modelPath;
            _mlContext = new MLContext();
        }

        public void Train(MonitorContext monitorContext)
        {
            
            // Fetch data from the database
            // Fetch and project data from the database
            var localPingInfos = monitorContext.PingInfos
                .Select(p => new LocalPingInfo
                {
                    DateSentInt = p.DateSentInt,
                    RoundTripTime = p.RoundTripTime
                })
                .ToList();

            // Load data into ML.NET data view
            var data = _mlContext.Data.LoadFromEnumerable(localPingInfos);

            var pipeline = _mlContext.Transforms.Concatenate("Features", nameof(LocalPingInfo.DateSentInt))
                .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                .Append(_mlContext.Regression.Trainers.Sdca(labelColumnName: nameof(LocalPingInfo.RoundTripTime), maximumNumberOfIterations: 100));

            var model = pipeline.Fit(data);

            _mlContext.Model.Save(model, data.Schema, _modelPath);
        }
    }
}
