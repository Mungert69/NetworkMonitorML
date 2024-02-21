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

           IMLModelFactory mlModelFactory=new MLModelFactory();
            var service = new MonitorMLService(_loggerMock.Object, _monitorMLDataRepoMock.Object, mlModelFactory);
            service.PredictWindow = predictWindow;
            // Act
            var result = await service.CheckHost(monitorIPID);

            // Assert
            Assert.True(result.Success);
            //_loggerMock.Verify(x => x.LogInformation(It.IsAny<string>()), Times.AtLeastOnce);
        }

        // Additional tests as necessary
    }
}
