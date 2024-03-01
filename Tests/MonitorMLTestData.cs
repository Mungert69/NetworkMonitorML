

using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.Connection;
using NetworkMonitor.Objects.ServiceMessage;
using System;
using System.Collections.Generic;

namespace NetworkMonitor.MonitorML.Tests;
public class MonitorMLTestData
{

 public static MonitorPingInfo GenerateLargeDataset(int monitorIPID, int dataSetID)
        {

            //int dataSetID = 0; // Assuming a current dataset
            int totalMinutes = 7 * 60;
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
        public static MonitorPingInfo GenerateDataWithChange(int monitorIPID, int dataSetID)
        {
            //int dataSetID = 0;
            int totalMinutes = 7 * 60;
            ushort normalPingTime = 50;
            ushort changedPingTime = 70; // Simulated change in ping time
            int changeStart = 200; // Change starts in the middle of the dataset

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
        public static MonitorPingInfo GenerateDataWithSpikeAndChange(int monitorIPID, int dataSetID)
        {
            //int dataSetID = 0;
            int totalMinutes = 7 * 60;
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
 public static MonitorPingInfo GenerateDataWithNoDetection(int monitorIPID, int dataSetID)
        {
            //int dataSetID = 0;
            int totalMinutes = 7 * 60;
            ushort normalPingTime = 50;
          
            var pingInfos = new List<PingInfo>();

            for (int i = 0; i < totalMinutes; i++)
            {
                ushort currentPingTime = normalPingTime;
                
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

        public static MonitorPingInfo GenerateSmallDataWithNoDetection(int monitorIPID, int dataSetID)
        {
            //int dataSetID = 0;
            int totalMinutes = 10;
            ushort normalPingTime = 50;
          
            var pingInfos = new List<PingInfo>();

            for (int i = 0; i < totalMinutes; i++)
            {
                ushort currentPingTime = normalPingTime;
                
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



    public static SystemParams GetSystemParams()
    {
        var systemParams = new SystemParams
        {
            ServiceID = "test",
            ServiceAuthKey="testkey"
        };
        return systemParams;
    }
    public static List<ProcessorObj> GetProcesorList()
    {
        var processorList = new List<ProcessorObj>();
        processorList.Add(new ProcessorObj() { AppID = "test" });
        return processorList;
    }
   
    public static DetectionResult GetDetectionResult(bool flag)
    {
        return new DetectionResult()
        {
            IsIssueDetected = flag
        };
    }
    public static List<IAlertable> GetPredictAlerts()
    {
        var alerts = new List<IAlertable>();
        alerts.Add(new PredictStatusAlert()
        {
            ID = 1,
            UserID = "test",
            Address = "1.1.1.1",
            UserName = "",
            AppID = "test",
            EndPointType = "icmp",
            Timeout = 10000,
            AddUserEmail = "support@mahadeva.co.uk",
            // Assuming these properties come from StatusObj and are relevant
            AlertFlag = false,
            AlertSent = false,
            ChangeDetectionResult = GetDetectionResult(false),
            SpikeDetectionResult = GetDetectionResult(false),
            EventTime = DateTime.UtcNow,

            Message = "Timeout",
            MonitorPingInfoID = 1,
        });
        alerts.Add(new PredictStatusAlert()
        {
            ID = 2,
            UserID = "test",
            Address = "2.2.2.2",
            UserName = "",
            AppID = "test",
            EndPointType = "icmp",
            Timeout = 10000,
            // Assuming these properties come from StatusObj and are relevant
            AlertFlag = true,
            AlertSent = false,
            ChangeDetectionResult = GetDetectionResult(true),
            SpikeDetectionResult = GetDetectionResult(true),
            EventTime = DateTime.UtcNow,

            Message = "Timeout",
            MonitorPingInfoID = 2,
        });
        alerts.Add(new PredictStatusAlert()
        {
            ID = 3,
            UserID = "nonuser",
            Address = "2.2.2.2",
            UserName = "",
            AppID = "test",
            EndPointType = "icmp",
            Timeout = 10000,
            AddUserEmail = "support@mahadeva.co.uk",
            // Assuming these properties come from StatusObj and are relevant
            AlertFlag = false,
            AlertSent = false,
            ChangeDetectionResult = GetDetectionResult(true),
            SpikeDetectionResult = GetDetectionResult(true),
            EventTime = DateTime.UtcNow,
            Message = "Timeout",
            MonitorPingInfoID = 3,
        });
        alerts.Add(new PredictStatusAlert()
        {
            ID = 4,
            UserID = "default",
            Address = "2.2.2.2",
            UserName = "",
            AppID = "test",
            EndPointType = "icmp",
            Timeout = 10000,
            AddUserEmail = "bademail@bademail",
            // Assuming these properties come from StatusObj and are relevant
            AlertFlag = true,
            AlertSent = false,

            EventTime = DateTime.UtcNow,
            ChangeDetectionResult = GetDetectionResult(true),
            SpikeDetectionResult = GetDetectionResult(true),
            Message = "Timeout",
            MonitorPingInfoID = 4,
        });
        return alerts;
    }

    public static void AddDataPredictAlerts(List<IAlertable> alerts)
    {
        alerts.Add(new PredictStatusAlert()
        {
            ID = 4,
            UserID = "default",
            Address = "2.2.2.2",
            UserName = "",
            AppID = "badappid",
            EndPointType = "icmp",
            Timeout = 10000,
            AddUserEmail = "bademail@bademail",
            // Assuming these properties come from StatusObj and are relevant
            AlertFlag = true,
            AlertSent = false,

            EventTime = DateTime.UtcNow,
            ChangeDetectionResult = GetDetectionResult(true),
            SpikeDetectionResult = GetDetectionResult(true),
            Message = "Timeout",
            MonitorPingInfoID = 5,
        });
    }
    
   
    public static List<UserInfo> GetUserInfos()
    {
        var userInfos = new List<UserInfo>();
        userInfos.Add(new UserInfo()
        {
            UserID = "test",
            Email = "support@mahadeva.co.uk",
            Email_verified = true,
            DisableEmail = false,
            Name = "test user"

        });
        userInfos.Add(new UserInfo()
        {
            UserID = "default",
            Email = "support@mahadeva.co.uk",
            Email_verified = true,
            DisableEmail = false,
            Name = "default"

        });
        return userInfos;
    }

}