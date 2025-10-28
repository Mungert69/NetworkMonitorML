using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.ML.Model;
using NetworkMonitor.ML.Services;
using NetworkMonitor.ML.Data;
using NetworkMonitor.Objects;
using NetworkMonitor.Utils.Helpers;
using NetworkMonitor.Objects.Repository;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System;
using Xunit;

namespace NetworkMonitor.MonitorML.Tests
{
    public class MonitorMLServiceTests
    {
        private readonly Mock<ILogger<MonitorMLService>> _loggerMock;
        private readonly Mock<IMLModelFactory> _mlModelFactoryMock;
        private readonly Mock<IMonitorMLDataRepo> _monitorMLDataRepoMock;
        private readonly Mock<IRabbitRepo> _rabbitRepoMock;
        private readonly Mock<ISystemParamsHelper> _systemParamsHelperMock;

        public MonitorMLServiceTests()
        {
            _loggerMock = new Mock<ILogger<MonitorMLService>>();
            _mlModelFactoryMock = new Mock<IMLModelFactory>();
            _monitorMLDataRepoMock = new Mock<IMonitorMLDataRepo>();
            _rabbitRepoMock = new Mock<IRabbitRepo>();
            _systemParamsHelperMock = new Mock<ISystemParamsHelper>();

        }


        [Fact]
        public async Task CheckHost_ReturnsSuccessWhenPredictionsArePositive()
        {
            // Arrange
            int monitorIPID = 1; // Example monitor ID
            int predictWindow = 50;
            int dataSetID = 0;
            var mockMonitorPingInfo = MonitorMLTestData.GenerateLargeDataset(monitorIPID, dataSetID);


            // Creating mockLocalPingInfos from mockMonitorPingInfo
            var mockLocalPingInfos = mockMonitorPingInfo.PingInfos.Select(pingInfo => new LocalPingInfo
            {
                DateSentInt = pingInfo.DateSentInt,
                RoundTripTime = pingInfo.RoundTripTime ?? 0, // Assuming RoundTripTime is nullable; replace with a default or error value if null
                StatusID = pingInfo.StatusID
            }).ToList();
            var systemParams = MonitorMLTestData.GetSystemParams();
               var mlParams = MonitorMLTestData.GetMLParams();
             _systemParamsHelperMock.Setup(p => p.GetMLParams()).Returns(mlParams);
            // Setup _systemParamsHelperMock to return the mocked SystemParams object from GetSystemParams()
            _systemParamsHelperMock.Setup(p => p.GetSystemParams()).Returns(systemParams);

            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>(), dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, dataSetID))
                                .ReturnsAsync(mockMonitorPingInfo);

            
            _monitorMLDataRepoMock.Setup(repo => repo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, It.IsAny<PredictStatus>()))
                                              .ReturnsAsync(new ResultObj());
            IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory, _rabbitRepoMock.Object, _systemParamsHelperMock.Object);
            service.PredictWindow = predictWindow;
            service.SpikeDetectionThreshold = 2;
            service.ChangeConfidence = 90;
            service.ChangePreTrain = 20;
            service.SpikePreTrain = 20;
            // Act
            var result = await service.CheckHost(monitorIPID, dataSetID);

            // Assert
            Assert.True(result.Success, $"The prediction did not compete with success. Message: {result.Message}");
            var detectionResult = result.Data;

            // Now you can assert specific aspects of the DetectionResult
            Assert.True(!detectionResult.ChangeResult.IsIssueDetected, "A change was detected.");
            Assert.True(detectionResult.ChangeResult.NumberOfDetections == 0, "Changes were detected.");
            Assert.True(detectionResult.SpikeResult.IsIssueDetected, "No spike was detected.");
            Assert.True(detectionResult.SpikeResult.NumberOfDetections == 3, "Three spikes not detected.");
            Assert.True(detectionResult.SpikeResult.AverageScore == 1000, "The average score is out of the expected range.");
            Assert.False(mockMonitorPingInfo.PredictStatus?.AlertFlag ?? true, "Alert should remain false when only spike detection fires.");

        }

        [Fact]
        public async Task CheckHost_ReturnsAccuratePredictionWithPatternChange()
        {
            // Arrange
            int monitorIPID = 2; // Example monitor ID for this test
            int predictWindow = 50; // Window for predictions
            int dataSetID = 0;
            var mockMonitorPingInfo = MonitorMLTestData.GenerateDataWithChange(monitorIPID, dataSetID);
            var systemParams = MonitorMLTestData.GetSystemParams();
               var mlParams = MonitorMLTestData.GetMLParams();
             _systemParamsHelperMock.Setup(p => p.GetMLParams()).Returns(mlParams);
            // Setup _systemParamsHelperMock to return the mocked SystemParams object from GetSystemParams()
            _systemParamsHelperMock.Setup(p => p.GetSystemParams()).Returns(systemParams);

            // Mocking the repository to return the changed dataset
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>(), dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, dataSetID))
                               .ReturnsAsync(mockMonitorPingInfo);

            _monitorMLDataRepoMock.Setup(repo => repo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, It.IsAny<PredictStatus>()))
                                              .ReturnsAsync(new ResultObj());
            // Assume the model can handle pattern changes effectively
            // Further setup for ML model to predict based on changed data could be here

            IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory, _rabbitRepoMock.Object, _systemParamsHelperMock.Object);
            service.PredictWindow = predictWindow;
            service.SpikeDetectionThreshold = 2;
            service.ChangeConfidence = 90;
            service.ChangePreTrain = 20;
            service.SpikePreTrain = 20;
            // Act

            // Act
            var result = await service.CheckHost(monitorIPID, dataSetID);

            // Assert
            // Assuming that an accurate prediction with changed pattern means success
            Assert.True(result.Success, $"The prediction did not compete with success. Message: {result.Message}");
            var detectionResult = result.Data;

            // Now you can assert specific aspects of the DetectionResult
            Assert.True(detectionResult.ChangeResult.IsIssueDetected, "A change was not detected.");
            Assert.True(detectionResult.ChangeResult.NumberOfDetections == 1, $"{detectionResult.ChangeResult.NumberOfDetections} changes detected.");
            Assert.False(mockMonitorPingInfo.PredictStatus?.AlertFlag ?? true, "Alert should remain false until both change and spike detections fire.");
            //Assert.InRange(changeResult.MinPValue, 0, pValueThreshold, "The minimum p-value is out of the expected range.");
            // Adjust 'thresholdLow', 'thresholdHigh', and 'pValueThreshold' according to your expectations
        }

        [Fact]
        public async Task CheckHost_ReturnsAccuratePredictionWithSpikeAndChange()
        {
            // Arrange
            int monitorIPID = 3; // Example monitor ID for this scenario
            int predictWindow = 50; // Window for predictions
            int dataSetID = 0;
            var mockMonitorPingInfo = MonitorMLTestData.GenerateDataWithSpikeAndChange(monitorIPID, dataSetID);
            var systemParams = MonitorMLTestData.GetSystemParams();
               var mlParams = MonitorMLTestData.GetMLParams();
             _systemParamsHelperMock.Setup(p => p.GetMLParams()).Returns(mlParams);
            // Setup _systemParamsHelperMock to return the mocked SystemParams object from GetSystemParams()
            _systemParamsHelperMock.Setup(p => p.GetSystemParams()).Returns(systemParams);

            // Mocking the repository to return the dataset with both spikes and changes
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>(), dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, dataSetID))
         .ReturnsAsync(mockMonitorPingInfo);

            _monitorMLDataRepoMock.Setup(repo => repo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, It.IsAny<PredictStatus>()))
                                  .ReturnsAsync(new ResultObj());

            // Further setup for ML model to predict based on data with spikes and changes could be here
            // This may involve mocking the model's response to such data or ensuring the model factory produces a model capable of handling this complexity

            IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory, _rabbitRepoMock.Object, _systemParamsHelperMock.Object);
            service.PredictWindow = predictWindow;
            service.SpikeDetectionThreshold = 2;
            service.ChangeConfidence = 90;
            service.ChangePreTrain = 20;
            service.SpikePreTrain = 20;
            // Act

            // Act
            var result = await service.CheckHost(monitorIPID, dataSetID);
            Assert.True(result.Success, $"The prediction did not compete with success. Message: {result.Message}");
            var detectionResult = result.Data;

            // Now you can assert specific aspects of the DetectionResult
            Assert.True(detectionResult.ChangeResult.IsIssueDetected, "No change was detected.");
            Assert.True(detectionResult.ChangeResult.NumberOfDetections == 1, "More than one Changes were detected.");
            Assert.True(detectionResult.SpikeResult.IsIssueDetected, "No spike was detected.");
            Assert.True(detectionResult.SpikeResult.NumberOfDetections == 4, "Five spikes not detected.");
            Assert.True(detectionResult.SpikeResult.AverageScore == 767.5, "The average score is out of the expected range.");
            Assert.True(mockMonitorPingInfo.PredictStatus?.AlertFlag ?? false, "Alert should be raised when both spike and change detections align.");
        }

        [Fact]
        public async Task HybridPipeline_AllowsTimesFmToConfirmAlert()
        {
            // Arrange
            int monitorIPID = 10;
            int dataSetID = 0;
            var mockMonitorPingInfo = MonitorMLTestData.GenerateDataWithSpikeAndChange(monitorIPID, dataSetID);
            mockMonitorPingInfo.PredictStatus ??= new PredictStatus();

            var systemParams = MonitorMLTestData.GetSystemParams();
            var mlParams = MonitorMLTestData.GetHybridMLParams();

            _systemParamsHelperMock.Setup(p => p.GetMLParams()).Returns(mlParams);
            _systemParamsHelperMock.Setup(p => p.GetSystemParams()).Returns(systemParams);

            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>(), dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);
            _monitorMLDataRepoMock.Setup(repo => repo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, It.IsAny<PredictStatus>()))
                                  .ReturnsAsync(new ResultObj());
            _monitorMLDataRepoMock.Setup(repo => repo.GetLatestMonitorPingInfos(It.IsAny<int>()))
                                  .ReturnsAsync(new List<MonitorPingInfo> { mockMonitorPingInfo });

            IMLModelFactory primaryFactory = new FakeModelFactory(changeDetect: true, spikeDetect: true);
            ISecondaryModelFactory secondaryFactory = new FakeModelFactory(changeDetect: false, spikeDetect: true);

            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, primaryFactory, _rabbitRepoMock.Object, _systemParamsHelperMock.Object, secondaryFactory);
            var result = await service.CheckHost(monitorIPID, dataSetID);

            Assert.True(result.Success);
            Assert.False(result.Data.ChangeResult.IsIssueDetected);
            Assert.True(result.Data.SpikeResult.IsIssueDetected);
            Assert.True(mockMonitorPingInfo.PredictStatus?.AlertFlag ?? false);
            Assert.Contains("Secondary (TimesFM)", result.Message);
        }

        [Fact]
        public async Task HybridPipeline_SuppressesAlertWhenTimesFmRejects()
        {
            // Arrange
            int monitorIPID = 11;
            int dataSetID = 0;
            var mockMonitorPingInfo = MonitorMLTestData.GenerateDataWithSpikeAndChange(monitorIPID, dataSetID);
            mockMonitorPingInfo.PredictStatus ??= new PredictStatus();

            var systemParams = MonitorMLTestData.GetSystemParams();
            var mlParams = MonitorMLTestData.GetHybridMLParams();

            _systemParamsHelperMock.Setup(p => p.GetMLParams()).Returns(mlParams);
            _systemParamsHelperMock.Setup(p => p.GetSystemParams()).Returns(systemParams);

            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>(), dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);
            _monitorMLDataRepoMock.Setup(repo => repo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, It.IsAny<PredictStatus>()))
                                  .ReturnsAsync(new ResultObj());
            _monitorMLDataRepoMock.Setup(repo => repo.GetLatestMonitorPingInfos(It.IsAny<int>()))
                                  .ReturnsAsync(new List<MonitorPingInfo> { mockMonitorPingInfo });

            IMLModelFactory primaryFactory = new FakeModelFactory(changeDetect: true, spikeDetect: true);
            ISecondaryModelFactory secondaryFactory = new FakeModelFactory(changeDetect: false, spikeDetect: false);

            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, primaryFactory, _rabbitRepoMock.Object, _systemParamsHelperMock.Object, secondaryFactory);
            var result = await service.CheckHost(monitorIPID, dataSetID);

            Assert.True(result.Success);
            Assert.False(result.Data.ChangeResult.IsIssueDetected);
            Assert.False(result.Data.SpikeResult.IsIssueDetected);
            Assert.False(mockMonitorPingInfo.PredictStatus?.AlertFlag ?? true);
            Assert.Contains("TimesFM vetoed alert", result.Message);
        }

        [Fact]
        public async Task CheckLatestHosts_CheckReturnLogic()
        {
            // Arrange
            int monitorIPID = 3; // Example monitor ID for this scenario
            int predictWindow = 50; // Window for predictions
            int dataSetID = 0;
            var mockMonitorPingInfos = new List<MonitorPingInfo>();
            mockMonitorPingInfos.Add(MonitorMLTestData.GenerateDataWithSpikeAndChange(1, 0));
            mockMonitorPingInfos.Add(MonitorMLTestData.GenerateDataWithNoDetection(2, 0));
            //mockMonitorPingInfos.Add(MonitorMLTestData.GenerateDataWithNoDetection(3, 1));
            mockMonitorPingInfos.Add(MonitorMLTestData.GenerateSmallDataWithNoDetection(3, 0));

            var systemParams = MonitorMLTestData.GetSystemParams();
            var mlParams = MonitorMLTestData.GetMLParams();
             _systemParamsHelperMock.Setup(p => p.GetMLParams()).Returns(mlParams);

            // Setup _systemParamsHelperMock to return the mocked SystemParams object from GetSystemParams()
            _systemParamsHelperMock.Setup(p => p.GetSystemParams()).Returns(systemParams);
           
            // Mocking the repository to return the dataset with both spikes and changes
            _monitorMLDataRepoMock.Setup(repo => repo.GetLatestMonitorPingInfos(It.IsAny<int>()))
                                  .ReturnsAsync(mockMonitorPingInfos);


            _monitorMLDataRepoMock.Setup(repo => repo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, It.IsAny<PredictStatus>()))
                .ReturnsAsync(new ResultObj());

            // Further setup for ML model to predict based on data with spikes and changes could be here
            // This may involve mocking the model's response to such data or ensuring the model factory produces a model capable of handling this complexity

            IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory, _rabbitRepoMock.Object, _systemParamsHelperMock.Object);
            service.PredictWindow = predictWindow;
            service.SpikeDetectionThreshold = 2;
            service.ChangeConfidence = 90;
            service.ChangePreTrain = 20;
            service.SpikePreTrain = 20;
            // Act

            // Act
            var result = await service.CheckLatestHostsTest();
            Assert.True(result.Success, $"CheckLatestHosts did not compete with success. Message: {result.Message}");

#pragma warning disable CS8602 // Nullable warning
            Assert.True(result.Data[0].Data.ChangeResult.IsIssueDetected, "No change was detected.");
            Assert.True(result.Data[0].Data.ChangeResult.NumberOfDetections == 1, "More than one Changes were detected.");
            Assert.True(result.Data[0].Data.SpikeResult.IsIssueDetected, "No spike was detected.");
            Assert.True(result.Data[0].Data.SpikeResult.NumberOfDetections == 4, "Five spikes not detected.");
            Assert.True(result.Data[0].Data.SpikeResult.AverageScore == 767.5, "The average score is out of the expected range.");
            Assert.True(!result.Data[1].Data.ChangeResult.IsIssueDetected, "Change was detected.");
            Assert.True(result.Data[1].Data.ChangeResult.NumberOfDetections == 0, "Changes were detected.");
            Assert.True(!result.Data[1].Data.SpikeResult.IsIssueDetected, "Spike was detected.");
            Assert.True(result.Data[1].Data.SpikeResult.NumberOfDetections == 0, "Spikes were detected.");

#pragma warning restore CS8602 // Nullable warning           
        }


    

        [Fact]
        public async Task CheckHost_SkipsDetectionWhenAlertLatched()
        {
            int monitorIPID = 99;
            int dataSetID = 0;
            var mockMonitorPingInfo = MonitorMLTestData.GenerateDataWithSpikeAndChange(monitorIPID, dataSetID);
            mockMonitorPingInfo.PredictStatus ??= new PredictStatus();
            mockMonitorPingInfo.PredictStatus.AlertSent = true;
            mockMonitorPingInfo.PredictStatus.ChangeDetectionResult = new DetectionResult
            {
                IsIssueDetected = true,
                NumberOfDetections = 1,
                MaxMartingaleValue = 2.0,
                Result = new ResultObj { Success = true, Message = "cached change" }
            };
            mockMonitorPingInfo.PredictStatus.SpikeDetectionResult = new DetectionResult
            {
                IsIssueDetected = true,
                NumberOfDetections = 4,
                MaxMartingaleValue = 2.0,
                Result = new ResultObj { Success = true, Message = "cached spike" }
            };

            var systemParams = MonitorMLTestData.GetSystemParams();
            var mlParams = MonitorMLTestData.GetMLParams();
            _systemParamsHelperMock.Setup(p => p.GetMLParams()).Returns(mlParams);
            _systemParamsHelperMock.Setup(p => p.GetSystemParams()).Returns(systemParams);

            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>(), dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);

            _monitorMLDataRepoMock.Setup(repo => repo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, It.IsAny<PredictStatus>()))
                                  .ReturnsAsync(new ResultObj());

            IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory, _rabbitRepoMock.Object, _systemParamsHelperMock.Object);

            var result = await service.CheckHost(monitorIPID, dataSetID);

            Assert.True(result.Success);
            Assert.Contains("Skipped", result.Data.ChangeResult.Result.Message);
            Assert.Contains("Skipped", result.Data.SpikeResult.Result.Message);
            Assert.Contains("Skipped", mockMonitorPingInfo.PredictStatus.ChangeDetectionResult.Result.Message);
            Assert.Contains("Skipped", mockMonitorPingInfo.PredictStatus.SpikeDetectionResult.Result.Message);
        }

    private sealed class FakeModelFactory : IMLModelFactory, ISecondaryModelFactory
    {
        private readonly bool _detectChange;
        private readonly bool _detectSpike;

        public FakeModelFactory(bool changeDetect, bool spikeDetect)
        {
            _detectChange = changeDetect;
            _detectSpike = spikeDetect;
        }

        public IMLModel CreateModel(string modelType, int monitorPingInfoID, double confidence, int preTrain)
        {
            bool detect = string.Equals(modelType, "change", StringComparison.OrdinalIgnoreCase) ? _detectChange : _detectSpike;
            return new FakeModel(detect) { Confidence = confidence, PreTrain = preTrain };
        }
    }

    private sealed class FakeModel : IMLModel
    {
        private readonly bool _detect;

        public FakeModel(bool detect) => _detect = detect;

        public double Confidence { get; set; }
        public int PreTrain { get; set; }

        public void Train(List<LocalPingInfo> data) { }

        public AnomalyPrediction Predict(LocalPingInfo input) => CreatePrediction(_detect);

        public IEnumerable<AnomalyPrediction> PredictList(List<LocalPingInfo> inputs)
            => inputs.Select(_ => CreatePrediction(_detect)).ToList();

        public void PrintPrediction(IEnumerable<AnomalyPrediction> predictions) { }

        private static AnomalyPrediction CreatePrediction(bool detect)
        {
            return new AnomalyPrediction
            {
                Prediction = new[]
                {
                    detect ? 1d : 0d,
                    detect ? 100d : 10d,
                    detect ? 0.01d : 0.9d,
                    detect ? 50d : 5d
                }
            };
        }
    }
}
}
