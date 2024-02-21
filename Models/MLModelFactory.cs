namespace NetworkMonitor.ML.Model
{
     public interface IMLModelFactory
    {
        IMLModel CreateChangeDetectionModel(int monitorPingInfoID, double confidence);
        IMLModel CreateSpikeDetectionModel(int monitorPingInfoID, double confidence);
    }
    public class MLModelFactory : IMLModelFactory
    {
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
