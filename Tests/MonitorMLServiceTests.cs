using Microsoft.Extensions.Logging;
using Moq;
using NetworkMonitor.ML.Model;
using NetworkMonitor.ML.Services;
using NetworkMonitor.ML.Data;
using NetworkMonitor.Objects;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System;
using Xunit;

namespace NetworkMonitor.Tests
{
    public class MonitorMLServiceTests
    {
        private readonly Mock<ILogger<MonitorMLService>> _loggerMock;
        private readonly Mock<IMLModelFactory> _mlModelFactoryMock;
        private readonly Mock<IMonitorMLDataRepo> _monitorMLDataRepoMock;

        public MonitorMLServiceTests()
        {
            _loggerMock = new Mock<ILogger<MonitorMLService>>();
            _mlModelFactoryMock = new Mock<IMLModelFactory>();
            _monitorMLDataRepoMock = new Mock<IMonitorMLDataRepo>();
        }

        public MonitorPingInfo GenerateLargeDataset(int monitorIPID)
        {

            int dataSetID = 0; // Assuming a current dataset
            int totalMinutes = 7 * 60; // Data for one day
            ushort normalPingTime = 50; // Normal ping time in ms
            ushort spikePingTime = 1000; // Simulated spike in ping time in ms
            int spikeInterval = 120; // Spike every 120 minutes
            var pingInfos = new List<PingInfo>();

            for (int i = 0; i < totalMinutes; i++)
            {
                ushort currentPingTime = normalPingTime;
                if (i % spikeInterval == 0) // Introduce a spike every spikeInterval minutes
                {
                    currentPingTime = spikePingTime;
                }

                pingInfos.Add(new PingInfo
                {
                    DateSent = DateTime.UtcNow.AddMinutes(-totalMinutes + i),
                    RoundTripTime = currentPingTime,
                    StatusID = 1
                });
            }

            var mockMonitorPingInfo = new MonitorPingInfo
            {
                MonitorIPID = monitorIPID,
                DataSetID = dataSetID,
                PingInfos = pingInfos
            };

            return mockMonitorPingInfo;
        }
        public MonitorPingInfo GenerateDataWithChange(int monitorIPID)
        {
            int dataSetID = 1;
            int totalMinutes = 7 * 60; // Simulating data for one week in minutes
            ushort normalPingTime = 50;
            ushort changedPingTime = 70; // Simulated change in ping time
            int changeStart = totalMinutes / 2; // Change starts in the middle of the dataset

            var pingInfos = new List<PingInfo>();

            for (int i = 0; i < totalMinutes; i++)
            {
                ushort currentPingTime = i >= changeStart ? changedPingTime : normalPingTime;

                pingInfos.Add(new PingInfo
                {
                    DateSent = DateTime.UtcNow.AddMinutes(-totalMinutes + i),
                    RoundTripTime = currentPingTime,
                    StatusID = 1
                });
            }

            return new MonitorPingInfo
            {
                MonitorIPID = monitorIPID,
                DataSetID = dataSetID,
                PingInfos = pingInfos
            };
        }
        public MonitorPingInfo GenerateDataWithSpikeAndChange(int monitorIPID)
        {
            int dataSetID = 1;
            int totalMinutes = 7 * 60; // Simulating data for one week in minutes
            ushort normalPingTime = 50;
            ushort spikePingTime = 1000; // Spike
            ushort changedPingTime = 70; // Change in normal ping time
            int spikeInterval = 120;
            int changeStart = totalMinutes / 2;

            var pingInfos = new List<PingInfo>();

            for (int i = 0; i < totalMinutes; i++)
            {
                ushort currentPingTime = normalPingTime;
                if (i >= changeStart)
                {
                    currentPingTime = changedPingTime; // Apply change in pattern
                }
                if (i % spikeInterval == 0) // Spike logic applies throughout the dataset
                {
                    currentPingTime = spikePingTime;
                }

                pingInfos.Add(new PingInfo
                {
                    DateSent = DateTime.UtcNow.AddMinutes(-totalMinutes + i),
                    RoundTripTime = currentPingTime,
                    StatusID = 1
                });
            }

            return new MonitorPingInfo
            {
                MonitorIPID = monitorIPID,
                DataSetID = dataSetID,
                PingInfos = pingInfos
            };
        }


        [Fact]
        public async Task CheckHost_ReturnsSuccessWhenPredictionsArePositive()
        {
            // Arrange
            int monitorIPID = 1; // Example monitor ID
            int predictWindow = 50;
            var mockMonitorPingInfo = GenerateLargeDataset(monitorIPID);


            // Creating mockLocalPingInfos from mockMonitorPingInfo
            var mockLocalPingInfos = mockMonitorPingInfo.PingInfos.Select(pingInfo => new LocalPingInfo
            {
                DateSentInt = pingInfo.DateSentInt,
                RoundTripTime = pingInfo.RoundTripTime ?? 0, // Assuming RoundTripTime is nullable; replace with a default or error value if null
                StatusID = pingInfo.StatusID
            }).ToList();

            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>()))
                                  .ReturnsAsync(mockMonitorPingInfo);

            _monitorMLDataRepoMock.Setup(repo => repo.GetLocalPingInfosForHost(monitorIPID))
                                  .ReturnsAsync(mockLocalPingInfos);

            IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory);
            service.PredictWindow = predictWindow;
            // Act
            var result = await service.CheckHost(monitorIPID);

            // Assert
            Assert.True(result.Success);
            //_loggerMock.Verify(x => x.LogInformation(It.IsAny<string>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task CheckHost_ReturnsAccuratePredictionWithPatternChange()
        {
            // Arrange
            int monitorIPID = 2; // Example monitor ID for this test
            int predictWindow = 50; // Window for predictions
            var mockMonitorPingInfo = GenerateDataWithChange(monitorIPID);

            // Mocking the repository to return the changed dataset
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>()))
                                  .ReturnsAsync(mockMonitorPingInfo);

            // Assume the model can handle pattern changes effectively
            // Further setup for ML model to predict based on changed data could be here

              IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory);
            service.PredictWindow = predictWindow;
            // Act

            // Act
            var result = await service.CheckHost(monitorIPID);

            // Assert
            // Assuming that an accurate prediction with changed pattern means success
            Assert.True(result.Success, "The prediction was not accurate with the pattern change.");
        }

        [Fact]
        public async Task CheckHost_ReturnsAccuratePredictionWithSpikeAndChange()
        {
            // Arrange
            int monitorIPID = 3; // Example monitor ID for this scenario
            int predictWindow = 50; // Window for predictions
            var mockMonitorPingInfo = GenerateDataWithSpikeAndChange(monitorIPID);

            // Mocking the repository to return the dataset with both spikes and changes
            _monitorMLDataRepoMock.Setup(repo => repo.GetMonitorPingInfo(monitorIPID, It.IsAny<int>()))
                                  .ReturnsAsync(mockMonitorPingInfo);

            // Further setup for ML model to predict based on data with spikes and changes could be here
            // This may involve mocking the model's response to such data or ensuring the model factory produces a model capable of handling this complexity

                IMLModelFactory mlModelFactory = new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory);
            service.PredictWindow = predictWindow;
            // Act

            // Act
            var result = await service.CheckHost(monitorIPID);

            // Assert
            // Assuming accurate prediction in complex scenario equals success
            Assert.True(result.Success, "The prediction was not accurate with both spikes and changes in the data pattern.");
        }


    }

}
