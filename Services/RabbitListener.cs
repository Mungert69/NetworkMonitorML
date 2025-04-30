using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Objects;
using NetworkMonitor.Data.Services;
using System.Collections.Generic;
using System;
using System.Text;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NetworkMonitor.Utils;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects.Factory;
using NetworkMonitor.Utils.Helpers;
using NetworkMonitor.Objects.Repository;
using System.Net;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OneOf.Types;
namespace NetworkMonitor.ML.Services;

public interface IRabbitListener
{

    Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj);
    Task Shutdown();
    Task<ResultObj> Setup();


}

public class RabbitListener : RabbitListenerBase, IRabbitListener
{
    protected IMonitorMLService _mlService;

    public RabbitListener(IMonitorMLService mlService, ILogger<RabbitListenerBase> logger, ISystemParamsHelper systemParamsHelper) : base(logger, DeriveSystemUrl(systemParamsHelper))
    {

        _mlService = mlService;
    }

    private static SystemUrl DeriveSystemUrl(ISystemParamsHelper systemParamsHelper)
    {
        return systemParamsHelper.GetSystemParams().ThisSystemUrl;
    }
    protected override void InitRabbitMQObjs()
    {


        _rabbitMQObjs.Add(new RabbitMQObj()
        {
            ExchangeName = "mlCheck",
            FuncName = "mlCheck",
            MessageTimeout = 60000
        });
        _rabbitMQObjs.Add(new RabbitMQObj()
        {
            ExchangeName = "mlCheckHost",
            FuncName = "mlCheckHost",
            MessageTimeout = 60000
        });
        _rabbitMQObjs.Add(new RabbitMQObj()
        {
            ExchangeName = "mlCheckLatestHosts",
            FuncName = "mlCheckLatestHosts",
            MessageTimeout = 60000
        });
        _rabbitMQObjs.Add(new RabbitMQObj()
        {
            ExchangeName = "predictPingInfos",
            FuncName = "predictPingInfos",
            MessageTimeout = 60000
        });
        _rabbitMQObjs.Add(new RabbitMQObj()
        {
            ExchangeName = "predictAlertFlag",
            FuncName = "predictAlertFlag"
        });
        _rabbitMQObjs.Add(new RabbitMQObj()
        {
            ExchangeName = "predictAlertSent",
            FuncName = "predictAlertSent"
        });

        _rabbitMQObjs.Add(new RabbitMQObj()
        {
            ExchangeName = "predictResetAlerts",
            FuncName = "predictResetAlerts"
        });


    }
    protected override async Task<ResultObj> DeclareConsumers()
    {
        var result = new ResultObj();
        result.Success = true;
        try
        {
            await Parallel.ForEachAsync(_rabbitMQObjs, async (rabbitMQObj, cancellationToken) =>
               {

                   if (rabbitMQObj.ConnectChannel != null)
                   {

                       rabbitMQObj.Consumer = new AsyncEventingBasicConsumer(rabbitMQObj.ConnectChannel);
                       await rabbitMQObj.ConnectChannel.BasicConsumeAsync(
                               queue: rabbitMQObj.QueueName,
                               autoAck: false,
                               consumer: rabbitMQObj.Consumer
                           );


                       switch (rabbitMQObj.FuncName)
                       {
                           case "mlCheck":
                               await rabbitMQObj.ConnectChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
                               rabbitMQObj.Consumer.ReceivedAsync += async (model, ea) =>
                            {
                   try
                   {
                       result = await MLCheck(ConvertToObject<MonitorMLInitObj>(model, ea));
                       await rabbitMQObj.ConnectChannel.BasicAckAsync(ea.DeliveryTag, false);
                   }
                   catch (Exception ex)
                   {
                       _logger.LogError(" Error : RabbitListener.DeclareConsumers.mlCheck " + ex.Message);
                   }
               };
                               break;
                           case "mlCheckHost":
                               await rabbitMQObj.ConnectChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
                               rabbitMQObj.Consumer.ReceivedAsync += async (model, ea) =>
                            {
                   try
                   {
                       result = await CheckHost(ConvertToObject<MonitorMLCheckObj>(model, ea));
                       await rabbitMQObj.ConnectChannel.BasicAckAsync(ea.DeliveryTag, false);
                   }
                   catch (Exception ex)
                   {
                       _logger.LogError(" Error : RabbitListener.DeclareConsumers.mlCheckHost " + ex.Message);
                   }
               };
                               break;
                           case "mlCheckLatestHosts":
                               await rabbitMQObj.ConnectChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
                               rabbitMQObj.Consumer.ReceivedAsync += async (model, ea) =>
                            {
                   try
                   {
                       result = await CheckLatestHosts();
                       await rabbitMQObj.ConnectChannel.BasicAckAsync(ea.DeliveryTag, false);
                   }
                   catch (Exception ex)
                   {
                       _logger.LogError(" Error : RabbitListener.DeclareConsumers.mlCheckLatestHosts " + ex.Message);
                   }
               };
                               break;
                           case "predictPingInfos":
                               await rabbitMQObj.ConnectChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
                               rabbitMQObj.Consumer.ReceivedAsync += async (model, ea) =>
                            {
                   try
                   {
                       result = UpdatePingInfos(ConvertToObject<ProcessorDataObj>(model, ea));
                       await rabbitMQObj.ConnectChannel.BasicAckAsync(ea.DeliveryTag, false);
                   }
                   catch (Exception ex)
                   {
                       _logger.LogError(" Error : RabbitListener.DeclareConsumers.predictPingInfos " + ex.Message);
                   }
               };
                               break;
                           case "predictAlertFlag":
                               await rabbitMQObj.ConnectChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
                               rabbitMQObj.Consumer.ReceivedAsync += async (model, ea) =>
                            {
                           try
                           {
                               result = await AlertFlag(ConvertToList<List<int>>(model, ea));
                               await rabbitMQObj.ConnectChannel.BasicAckAsync(ea.DeliveryTag, false);
                           }
                           catch (Exception ex)
                           {
                               _logger.LogError(" Error : RabbitListener.DeclareConsumers.predictAlertFlag " + ex.Message);
                           }
                       };
                               break;
                           case "predictAlertSent":
                               await rabbitMQObj.ConnectChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
                               rabbitMQObj.Consumer.ReceivedAsync += async (model, ea) =>
                            {
                           try
                           {
                               result = await AlertSent(ConvertToList<List<int>>(model, ea));
                               await rabbitMQObj.ConnectChannel.BasicAckAsync(ea.DeliveryTag, false);
                           }
                           catch (Exception ex)
                           {
                               _logger.LogError(" Error : RabbitListener.DeclareConsumers.predictAlertSent " + ex.Message);
                           }
                       };
                               break;
                           case "predictResetAlerts":
                               await rabbitMQObj.ConnectChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);
                               rabbitMQObj.Consumer.ReceivedAsync += async (model, ea) =>
                            {
                           try
                           {
                               result = await ResetAlerts(ConvertToList<List<int>>(model, ea));
                               await rabbitMQObj.ConnectChannel.BasicAckAsync(ea.DeliveryTag, false);
                           }
                           catch (Exception ex)
                           {
                               _logger.LogError(" Error : RabbitListener.DeclareConsumers.predictResetAlerts " + ex.Message);
                           }
                       };
                               break;
                       }

                   }
               });
            if (result.Success) result.Message += " Success : Declared all consumers ";
        }
        catch (Exception e)
        {
            string message = " Error : failed to declare consumers. Error was : " + e.ToString() + " . ";
            result.Message += message;
            _logger.LogError(result.Message);
            result.Success = false;
        }
        return result;
    }
    public async Task<ResultObj> MLCheck(MonitorMLInitObj? serviceObj)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "MessageAPI : MLCheck : ";
        if (serviceObj == null)
        {
            result.Message += " Error : serviceObj is null.";
            _logger.LogError(result.Message);
            return result;
        }
        try
        {
            result = await _mlService.MLCheck(serviceObj);
            _logger.LogInformation(result.Message);
        }
        catch (Exception e)
        {
            result.Data = null;
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(result.Message);
        }
        return result;
    }
    public async Task<ResultObj> CheckHost(MonitorMLCheckObj? checkHostObj)
    {
        var tResult = new TResultObj<(DetectionResult ChangeResult, DetectionResult SpikeResult)>();
        tResult.Success = false;
        tResult.Message = "MessageAPI : CheckHost : ";
        var result = new ResultObj();
        if (checkHostObj == null)
        {
            result.Message += tResult.Message + "Error : chechHostObj is null";
            _logger.LogError(result.Message);
            result.Success = false;
            return result;

        }

        try
        {
            tResult = await _mlService.CheckHost(checkHostObj.MonitorIPID, checkHostObj.DataSetID);
            _logger.LogInformation(tResult.Message);
        }
        catch (Exception e)
        {
            tResult.Success = false;
            tResult.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(tResult.Message);
        }
        result = new ResultObj() { Success = tResult.Success, Message = tResult.Message, Data = tResult.Data };
        return result;
    }

    public async Task<ResultObj> CheckLatestHosts()
    {
        var result = new ResultObj();
        result.Success = false;
        result.Message = "MessageAPI : CheckLatestHosts : ";
        try
        {
            result = await _mlService.CheckLatestHosts();
            //_logger.LogInformation(result.Message);
        }
        catch (Exception e)
        {
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(result.Message);
        }
        return result;
    }
    public ResultObj UpdatePingInfos(ProcessorDataObj? processorDataObj)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "MessageAPI : UpdatePingInfos : ";
        if (processorDataObj == null)
        {
            result.Message += " Error : processorDataObj is null.";
            _logger.LogError(result.Message);
            return result;
        }
        try
        {
            result = _mlService.UpdatePingInfos(processorDataObj);
        }
        catch (Exception e)
        {
            result.Data = null;
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";

        }
        if (result.Success) _logger.LogInformation(result.Message);
        else _logger.LogError(result.Message);
        return result;
    }

    public async Task<ResultObj> AlertFlag(List<int>? monitorIPIDs)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "MessageAPI : AlertFlag : ";
        if (monitorIPIDs == null)
        {
            result.Success = false;
            result.Message += "Error : monitorIPIDs was null .";
            _logger.LogError(result.Message);
            return result;

        }
        try
        {
            monitorIPIDs.ForEach(f => _logger.LogDebug("AlertFlag Found monitorIPID=" + f));
            List<ResultObj> results = await _mlService.UpdateAlertFlag(monitorIPIDs, true);
            result.Success = results.Where(w => w.Success == false).ToList().Count() == 0;
            if (result.Success) result.Message += "Success ran ok ";
            else
            {
                results.Select(s => s.Message).ToList().ForEach(f => result.Message += f);
                result.Data = results;
            }
            _logger.LogInformation(result.Message);
        }
        catch (Exception e)
        {
            result.Data = null;
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(result.Message);
        }
        return result;
    }
    public async Task<ResultObj> AlertSent(List<int>? monitorIPIDs)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "MessageAPI : AlertSent : ";
        if (monitorIPIDs == null)
        {
            result.Success = false;
            result.Message += "Error : monitorIPIDs was null .";
            _logger.LogError(result.Message);
            return result;

        }
        try
        {
            monitorIPIDs.ForEach(f => _logger.LogDebug("SentFlag Found monitorIPID =" + f));
            List<ResultObj> results = await _mlService.UpdateAlertSent(monitorIPIDs, true);
            result.Success = results.Where(w => w.Success == false).ToList().Count() == 0;
            if (result.Success) result.Message += "Success ran ok ";
            else
            {
                results.Select(s => s.Message).ToList().ForEach(f => result.Message += f);
                result.Data = results;
            }
            _logger.LogInformation(result.Message);
        }
        catch (Exception e)
        {
            result.Data = null;
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(result.Message);
        }
        return result;
    }
    public async Task<ResultObj> ResetAlerts(List<int>? monitorIPIDs)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "MessageAPI : ResetAlerts : ";
        if (monitorIPIDs == null)
        {
            result.Success = false;
            result.Message += "Error : monitorIPIDs was null .";
            _logger.LogError(result.Message);
            return result;

        }
        try
        {
            var results = await _mlService.ResetAlerts(monitorIPIDs);
            results.ForEach(f => result.Message += f.Message);
            result.Success = results.All(a => a.Success == true) && results.Count() != 0;
            result.Data = results;
            if (result.Success == true)
                _logger.LogInformation(result.Message);
            else _logger.LogError(result.Message);
        }
        catch (Exception e)
        {
            result.Data = null;
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(result.Message);
        }
        return result;
    }


}
