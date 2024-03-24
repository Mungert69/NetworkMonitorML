using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Objects.Repository;


namespace NetworkMonitor.ML.Services;
// LLMService.cs
public interface ILLMService
{
    Task<LLMServiceObj> StartProcess(LLMServiceObj llmServiceObj);
    Task<LLMServiceObj> SendInputAndGetResponse(LLMServiceObj serviceObj);
}

public class LLMService : ILLMService
{
    private ILogger _logger;
    private readonly ILLMProcessRunner _processRunner;
        private IRabbitRepo _rabbitRepo;

    private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();
    // private readonly ILLMResponseProcessor _responseProcessor;

    public LLMService(ILogger<LLMService> logger, ILLMProcessRunner processRunner, IRabbitRepo rabbitRepo)
    {
        _processRunner = processRunner;
        _rabbitRepo = rabbitRepo;
        _logger = logger;
    }
    
    public async Task<LLMServiceObj> StartProcess(LLMServiceObj llmServiceObj)
    {
        string modelPath = "notset";
        llmServiceObj.SessionId = Guid.NewGuid().ToString();
        try
        {
            await _processRunner.StartProcess(llmServiceObj.SessionId, modelPath);
            _sessions[llmServiceObj.SessionId] = new Session();
            llmServiceObj.ResultMessage = " Success : LLMService Started Session .";
            llmServiceObj.ResultSuccess = true;
            await _rabbitRepo.PublishAsync<LLMServiceObj>("llmServiceStart", llmServiceObj);
        }
        catch (Exception e)
        {
            llmServiceObj.ResultMessage = e.Message;
            llmServiceObj.ResultSuccess = false;
         }


        return llmServiceObj;
    }

    public async Task<LLMServiceObj> SendInputAndGetResponse(LLMServiceObj serviceObj)
    {
        if (!_sessions.TryGetValue(serviceObj.SessionId, out var session)) { 
            serviceObj.ResultMessage="Invalid session ID";
            serviceObj.ResultSuccess = false;
            return serviceObj;
        }

        try { 
            var resultServiceObj= await _processRunner.SendInputAndGetResponse(serviceObj);
        }
        catch (Exception e) {
            serviceObj.ResultMessage = e.Message;
            serviceObj.ResultSuccess = false;
            return serviceObj;
        }
        return serviceObj;
    }

     public void EndSession(string sessionId)
    {
        _sessions.Remove(sessionId);
    }
}




// LLMResponseProcessor.cs
public interface ILLMResponseProcessor
{
    Task ProcessLLMOutput(LLMServiceObj serviceObj);
    Task ProcessFunctionCall(LLMServiceObj serviceObj);
    bool IsFunctionCallResponse(string input);
}

public class LLMResponseProcessor : ILLMResponseProcessor
{

    private IRabbitRepo _rabbitRepo;

    public LLMResponseProcessor(IRabbitRepo rabbitRepo)
    {

        _rabbitRepo = rabbitRepo;
    }

    public async Task ProcessLLMOutput(LLMServiceObj serviceObj)
    {
        Console.WriteLine(serviceObj.LlmMessage);
        await _rabbitRepo.PublishAsync<LLMServiceObj>("llmServiceMessage", serviceObj);
        //return Task.CompletedTask;
    }

    public async Task ProcessFunctionCall(LLMServiceObj serviceObj)
    {
        await _rabbitRepo.PublishAsync<LLMServiceObj>("llmServiceFunction", serviceObj);
        //var functionCallData = JsonSerializer.Deserialize<FunctionCallData>(serviceObj.JsonFunction);
        //await _functionExecutor.ExecuteFunction(serviceObj.SessionId,functionCallData);
    }

    public bool IsFunctionCallResponse(string input)
    {
        try
        {
            var jsonElement = JsonDocument.Parse(input).RootElement;
            return jsonElement.TryGetProperty("name", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}


public enum ResponseState { Initial, AwaitingInput, FunctionCallProcessed, Completed }

public class Session
{
    public List<string> Responses { get; } = new List<string>();
}