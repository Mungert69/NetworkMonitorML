using System;
using System.Collections.Generic;
using System.Linq;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Repository;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using NetworkMonitor.Utils;
namespace NetworkMonitor.ML.Repository
{
    public class PublishRepo
    {

        public static async Task<ResultObj> AlertMessgeResetAlerts(IRabbitRepo rabbitRepo, List<AlertFlagObj> alertFlagObjs, string appID, string authKey)
        {
            var result = new ResultObj();
            try
            {
                var alertServiceAlertObj = new AlertServiceAlertObj()
                {
                    AppID = appID,
                    AuthKey = authKey,
                    AlertFlagObjs = alertFlagObjs
                };

                await rabbitRepo.PublishAsync<AlertServiceAlertObj>("alertMessageResetAlerts", alertServiceAlertObj);
                //DaprRepo.PublishEvent<List<AlertFlagObj>>(_daprClient, "alertMessageResetAlerts", alertFlagObjs);
                result.Success = true;
                result.Message = " Success : sent alertMessageResetAlert message . ";
            }
            catch (Exception e)
            {
                result.Success = false;
                result.Message += " Error : failed to set alertMessageResetAlert. Error was :" + e.Message.ToString();
            }
            return result;
        }

        /* public static void ProcessorResetAlerts(ILogger logger, IRabbitRepo rabbitRepo, Dictionary<string, List<int>> monitorIPDic)
         {
             try
             {
                 foreach (KeyValuePair<string, List<int>> kvp in monitorIPDic)
                 {
                     var monitorIPIDs = new List<int>(kvp.Value);
                     // Dont publish this at the moment as its causing alerts to refire.
                     rabbitRepo.Publish<List<int>>("processorResetAlerts" + kvp.Key, monitorIPIDs);
                 }
             }
             catch (Exception e)
             {
                 logger.LogError(" Error : failed to publish ProcessResetAlerts. Error was :" + e.ToString());
             }
         }*/


        public static async Task<ResultObj> MonitorPingInfos(ILogger logger, IRabbitRepo rabbitRepo, List<MonitorPingInfo> monitorPingInfos, string appID, string authKey)
        {
            // var _daprMetadata = new Dictionary<string, string>();
            //_daprMetadata.Add("ttlInSeconds", "120");
            var result = new ResultObj();
            string timerStr = "TIMER started : ";
            result.Message = "PublishMonitorPingInfos : ";
            var timer = new Stopwatch();
            timer.Start();
            try
            {
                if (monitorPingInfos != null && monitorPingInfos.Count() != 0)
                {
                    int countMonPingInfos = monitorPingInfos.Count();
                    //var cutMonitorPingInfos = monitorPingInfos.ConvertAll(x => new MonitorPingInfo(x));
                    //timerStr += " Event (Created Cut MonitorPingInfos) at " + timer.ElapsedMilliseconds + " : ";
                    //var pingInfos = new List<PingInfo>();
                    var predictStatusAlerts = new List<PredictStatusAlert>();
                    foreach (var f in monitorPingInfos)
                    {
                        if (f.PredictStatus == null)
                        {
                            continue;
                        }

                        f.DateEnded = DateTime.UtcNow;
                        //pingInfos.AddRange(f.PingInfos.ToList());
                        var predictStatusAlert = new PredictStatusAlert();
                        predictStatusAlert.ID = f.MonitorIPID;
                        predictStatusAlert.AppID = appID;
                        predictStatusAlert.Address = f.Address;
                        predictStatusAlert.AlertFlag = f.PredictStatus.AlertFlag;
                        predictStatusAlert.AlertSent = f.PredictStatus.AlertSent;
                        predictStatusAlert.EventTime = f.PredictStatus.EventTime;
                        predictStatusAlert.SpikeDetectionResult = f.PredictStatus.SpikeDetectionResult;
                        predictStatusAlert.ChangeDetectionResult = f.PredictStatus.ChangeDetectionResult;
                        predictStatusAlert.Message = f.PredictStatus.Message;
                        predictStatusAlert.UserID = f.UserID;
                        predictStatusAlert.EndPointType = f.EndPointType;
                        predictStatusAlert.Timeout = f.Timeout;
                        predictStatusAlert.AddUserEmail = f.AddUserEmail;
                        predictStatusAlert.IsEmailVerified = f.IsEmailVerified;
                        predictStatusAlerts.Add(predictStatusAlert);
                    }
                    
                    //timerStr += " Event (Created All PingInfos as List) at " + timer.ElapsedMilliseconds + " : ";



                    var processorDataObjAlert = new ProcessorDataObj();
                    //processorDataObjAlert.MonitorPingInfos = null;
                    processorDataObjAlert.PredictStatusAlerts = predictStatusAlerts;
                    //processorDataObjAlert.PingInfos = new List<PingInfo>();
                    processorDataObjAlert.AppID = appID;
                    processorDataObjAlert.AuthKey = authKey;
                    int countMonStatusAlerts = predictStatusAlerts.Count();
                    timerStr += " Event (Finished ProcessorDataObj Setup) at " + timer.ElapsedMilliseconds + " : ";
                    await rabbitRepo.PublishJsonZAsync<ProcessorDataObj>("alertUpdatePredictStatusAlerts", processorDataObjAlert);
                    timerStr += $" Event (Published {countMonStatusAlerts} predictStatusAlerts to alertservice) at " + timer.ElapsedMilliseconds + " : ";
                    logger.LogDebug(" Sent ProcessorDataObjAlert to Alert Service :  " + JsonUtils.WriteJsonObjectToString<ProcessorDataObj>(processorDataObjAlert));

                }
                logger.LogInformation(timerStr);
                timer.Stop();
                result.Message += " Success : published PredictMonitorStatuses . ";
                result.Success = true;
                logger.LogInformation(result.Message);
            }
            catch (Exception e)
            {
                result.Message += " Error : Failed to publish events and save data to statestore. Error was : " + e.Message.ToString() + " . ";
                result.Success = false;
                logger.LogError(result.Message);
            }
            return result;
        }
        /*public static void ProcessorReadyThreadOLD(ILogger logger, DaprClient daprClient, string appID, bool isReady)
        {
            Thread thread = new Thread(delegate ()
                                    {
                                        PublishRepo.ProcessorReady(logger, daprClient, appID, isReady);
                                    });
            thread.Start();
        }
        private static void ProcessorReady(ILogger logger, DaprClient daprClient, string appID, bool isReady)
        {
            var processorObj = new ProcessorInitObj();
            processorObj.IsProcessorReady = isReady;
            processorObj.AppID = appID;
            DaprRepo.PublishEvent<ProcessorInitObj>(daprClient, "processorReady", processorObj);
            logger.LogInformation(" Published event ProcessorItitObj.IsProcessorReady = false ");
        }*/
        public static async Task PredictReady(ILogger logger, IRabbitRepo rabbitRepo, string appID, bool isReady)
        {
            try
            {
                var processorObj = new ProcessorInitObj();
                processorObj.IsProcessorReady = isReady;
                processorObj.AppID = appID;
                await rabbitRepo.PublishAsync<ProcessorInitObj>("predictReady", processorObj);
                logger.LogInformation(" Published event ProcessorItitObj.IsPredictReady = " + isReady);
            }
            catch (Exception e)
            {
                logger.LogError(" Error : Could not Publish event ProcessorItitObj.IsPredictReady . Erro was : " + e.Message);
            }
        }

    }
}
