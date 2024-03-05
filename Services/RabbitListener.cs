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
namespace NetworkMonitor.ML.Services;

public interface IRabbitListener
{

    Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj);


}

public class RabbitListener : RabbitListenerBase, IRabbitListener
{
    protected IMonitorMLService _mlService;

    public RabbitListener(IMonitorMLService mlService, ILogger<RabbitListenerBase> logger, ISystemParamsHelper systemParamsHelper) : base(logger, DeriveSystemUrl(systemParamsHelper))
    {

        _mlService = mlService;
        Setup();
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

    }
    protected override ResultObj DeclareConsumers()
    {
        var result = new ResultObj();
        result.Success = true;
        try
        {
            _rabbitMQObjs.ForEach(rabbitMQObj =>
        {
            if (rabbitMQObj.ConnectChannel == null)
            {
                result.Message += $" Error : RabbitListener Connect Channel not open for Exchange {rabbitMQObj.ExchangeName}";
                result.Success = false;
                _logger.LogCritical(result.Message);
                return;
            }
            rabbitMQObj.Consumer = new EventingBasicConsumer(rabbitMQObj.ConnectChannel);

            if (rabbitMQObj.Consumer == null)
            {
                result.Message += $" Error : RabbitListener can't create Consumer for queue  {rabbitMQObj.QueueName}";
                result.Success = false;
                _logger.LogCritical(result.Message);
                return;
            }
            switch (rabbitMQObj.FuncName)
            {
                case "mlCheck":
                    rabbitMQObj.ConnectChannel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
                    rabbitMQObj.Consumer.Received += async (model, ea) =>
                {
                    try
                    {
                        result = await MLCheck(ConvertToObject<MonitorMLInitObj>(model, ea));
                        rabbitMQObj.ConnectChannel.BasicAck(ea.DeliveryTag, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(" Error : RabbitListener.DeclareConsumers.mlCheck " + ex.Message);
                    }
                };
                    break;
                case "mlCheckHost":
                    rabbitMQObj.ConnectChannel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
                    rabbitMQObj.Consumer.Received += async (model, ea) =>
                {
                    try
                    {
                        result = await CheckHost(ConvertToObject<MonitorMLCheckObj>(model, ea));
                        rabbitMQObj.ConnectChannel.BasicAck(ea.DeliveryTag, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(" Error : RabbitListener.DeclareConsumers.mlCheckHost " + ex.Message);
                    }
                };
                    break;
                     case "mlCheckLatestHosts":
                    rabbitMQObj.ConnectChannel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
                    rabbitMQObj.Consumer.Received += async (model, ea) =>
                {
                    try
                    {
                        result = await CheckLatestHosts();
                        rabbitMQObj.ConnectChannel.BasicAck(ea.DeliveryTag, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(" Error : RabbitListener.DeclareConsumers.mlCheckLatestHosts " + ex.Message);
                    }
                };
                    break;
                    case "predictPingInfos":
                    rabbitMQObj.ConnectChannel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
                    rabbitMQObj.Consumer.Received +=  (model, ea) =>
                {
                    try
                    {
                        result = UpdatePingInfos(ConvertToObject<ProcessorDataObj>(model, ea));
                        rabbitMQObj.ConnectChannel.BasicAck(ea.DeliveryTag, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(" Error : RabbitListener.DeclareConsumers.predictPingInfos " + ex.Message);
                    }
                };
                    break;

            }
        });
            if (result.Success) result.Message += " Success : Declared all consumers ";
        }
        catch (Exception e)
        {
            string message = " Error : failed to declare consumers. Error was : " + e.ToString() + " . ";
            result.Message += message;
            Console.WriteLine(result.Message);
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
        if (checkHostObj == null)
        {
            return new ResultObj() { Message = " Error : chechHostObj is null." };
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
        var result = new ResultObj() { Success = tResult.Success, Message = tResult.Message, Data = tResult.Data };
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
            _logger.LogInformation(result.Message);
        }
        catch (Exception e)
        {
            result.Success = false;
            result.Message += "Error : Failed to receive message : Error was : " + e.Message + " ";
            _logger.LogError(result.Message);
        }
        return result;
    }
public  ResultObj UpdatePingInfos(ProcessorDataObj? processorDataObj)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "MessageAPI : UpdatePingInfos : ";
        if (processorDataObj == null)
        {
            result.Message += " Error : processorDataObj is null.";
            return result;
        }
        try
        {
            result = _mlService.UpdatePingInfos(processorDataObj);
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

}
