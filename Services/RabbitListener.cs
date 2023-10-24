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

            }
        });
            if (result.Success) result.Message += " Success : Declared all consumers ";
        }
        catch (Exception e)
        {
            result.Message += " Error : failed to declare consumers. Error was : " + e.ToString() + " . ";
            _logger.LogCritical(result.Message);
            result.Success = false;
        }
        return result;
    }
    public async Task<ResultObj> MLCheck(MonitorMLInitObj serviceObj)
    {
        ResultObj result = new ResultObj();
        result.Success = false;
        result.Message = "MessageAPI : MLCheck : ";
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

}
