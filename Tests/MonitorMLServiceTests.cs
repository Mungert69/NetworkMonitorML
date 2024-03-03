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
            // Setup _systemParamsHelperMock to return the mocked SystemParams object from GetSystemParams()
            _systemParamsHelperMock.Setup(p => p.GetSystemParams()).Returns(systemParams);

            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>(), dataSetID))
                                  .ReturnsAsync(mockMonitorPingInfo);
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, dataSetID))
                                .ReturnsAsync(mockMonitorPingInfo);

            _monitorMLDataRepoMock.Setup(repo => repo.GetLocalPingInfosForHost(monitorIPID))
                                  .ReturnsAsync(mockLocalPingInfos);
            _monitorMLDataRepoMock.Setup(repo => repo.UpdateMonitorPingInfoWithPredictionResultsById(monitorIPID, dataSetID, It.IsAny<PredictStatus>()))
                                              .ReturnsAsync(new ResultObj());
            IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory, _rabbitRepoMock.Object,_systemParamsHelperMock.Object);
            service.PredictWindow = predictWindow;
            service.SpikeDetectionThreshold = 2;
            service.ChangeConfidence = 90;
            // Act
            var result = await service.CheckHost(monitorIPID, dataSetID);

            // Assert
            Assert.True(result.Success, "The prediction did not compete with success.");
            var detectionResult = result.Data;

            // Now you can assert specific aspects of the DetectionResult
            Assert.True(!detectionResult.ChangeResult.IsIssueDetected, "A change was detected.");
            Assert.True(detectionResult.ChangeResult.NumberOfDetections == 0, "Changes were detected.");
            Assert.True(detectionResult.SpikeResult.IsIssueDetected, "No spike was detected.");
            Assert.True(detectionResult.SpikeResult.NumberOfDetections == 3, "Three spikes not detected.");
            Assert.True(detectionResult.SpikeResult.AverageScore == 1000, "The average score is out of the expected range.");

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
            // Act

            // Act
            var result = await service.CheckHost(monitorIPID, dataSetID);

            // Assert
            // Assuming that an accurate prediction with changed pattern means success
            Assert.True(result.Success, "The prediction did not compete with success.");
            var detectionResult = result.Data;

            // Now you can assert specific aspects of the DetectionResult
            Assert.True(detectionResult.ChangeResult.IsIssueDetected, "A change was not detected.");
            Assert.True(detectionResult.ChangeResult.NumberOfDetections == 1, "No Changes were detected.");
            Assert.True(!detectionResult.SpikeResult.IsIssueDetected, "A spike threshold was detected.");
            Assert.True(detectionResult.SpikeResult.NumberOfDetections == 2, "Two spikes not detected.");
            Assert.True(detectionResult.SpikeResult.AverageScore == 70, "The average score is out of the expected range.");
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
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory, _rabbitRepoMock.Object,_systemParamsHelperMock.Object);
            service.PredictWindow = predictWindow;
            service.SpikeDetectionThreshold = 2;
            service.ChangeConfidence = 90;
            // Act

            // Act
            var result = await service.CheckHost(monitorIPID, dataSetID);
            Assert.True(result.Success, "The prediction did not compete with success.");
            var detectionResult = result.Data;

            // Now you can assert specific aspects of the DetectionResult
            Assert.True(detectionResult.ChangeResult.IsIssueDetected, "No change was detected.");
            Assert.True(detectionResult.ChangeResult.NumberOfDetections == 1, "More than one Changes were detected.");
            Assert.True(detectionResult.SpikeResult.IsIssueDetected, "No spike was detected.");
            Assert.True(detectionResult.SpikeResult.NumberOfDetections == 5, "Five spikes not detected.");
            Assert.True(detectionResult.SpikeResult.AverageScore == 628, "The average score is out of the expected range.");
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
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory, _rabbitRepoMock.Object,_systemParamsHelperMock.Object);
            service.PredictWindow = predictWindow;
            service.SpikeDetectionThreshold = 2;
            service.ChangeConfidence = 90;
            // Act

            // Act
            var result = await service.CheckLatestHostsTest();
            Assert.True(result.Success, "CheckLatestHosts did not compete with success.");

#pragma warning disable CS8602 // Nullable warning
              Assert.True(result.Data[0].Data.ChangeResult.IsIssueDetected, "No change was detected.");
            Assert.True(result.Data[0].Data.ChangeResult.NumberOfDetections == 1, "More than one Changes were detected.");
            Assert.True(result.Data[0].Data.SpikeResult.IsIssueDetected, "No spike was detected.");
            Assert.True(result.Data[0].Data.SpikeResult.NumberOfDetections == 5, "Five spikes not detected.");
            Assert.True(result.Data[0].Data.SpikeResult.AverageScore == 628, "The average score is out of the expected range.");
         Assert.True(!result.Data[1].Data.ChangeResult.IsIssueDetected, "Change was detected.");
            Assert.True(result.Data[1].Data.ChangeResult.NumberOfDetections == 0, "Changes were detected.");
            Assert.True(!result.Data[1].Data.SpikeResult.IsIssueDetected, "Spike was detected.");
            Assert.True(result.Data[1].Data.SpikeResult.NumberOfDetections == 0, "Spikes were detected.");
            Assert.True(!result.Data[2].Success, " Reult was success.");
 #pragma warning restore CS8602 // Nullable warning           
              }


    }

}
