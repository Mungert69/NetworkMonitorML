using System;
namespace NetworkMonitor.ML.Model
{
    public interface IMLModelFactory
    {
        IMLModel CreateModel(string modelType, int monitorPingInfoID, double confidence);
        //IMLModel CreateChangeDetectionModel(int monitorPingInfoID, double confidence);
        //IMLModel CreateSpikeDetectionModel(int monitorPingInfoID, double confidence);
    }
    public class MLModelFactory : IMLModelFactory
    {
        
        public IMLModel CreateModel(string modelType, int monitorPingInfoID, double confidence)
        {
            switch (modelType.ToLower())
            {
                case "change":
                    return new ChangeDetectionModel(monitorPingInfoID, confidence);
                case "spike":
                    return new SpikeDetectionModel(monitorPingInfoID, confidence);
                default:
                    throw new ArgumentException($"Unknown model type: {modelType}", nameof(modelType));
            }
        }
        public IMLModel CreateChangeDetectionModel(int monitorPingInfoID, double confidence)
        {
            // Assuming ChangeDetectionModel constructor accepts monitorPingInfoID and confidence
            return new ChangeDetectionModel(monitorPingInfoID, confidence);
        }

        public IMLModel CreateSpikeDetectionModel(int monitorPingInfoID, double confidence)
        {
            // Assuming SpikeDetectionModel constructor accepts monitorPingInfoID and confidence
            return new SpikeDetectionModel(monitorPingInfoID, confidence);
        }
    }
}
