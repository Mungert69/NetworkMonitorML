using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Objects.Repository;


namespace NetworkMonitor.ML.Services;
// LLMService.cs
public interface ILLMService
{
    Task<LLMServiceObj> StartProcess(LLMServiceObj llmServiceObj);
    LLMServiceObj RemoveProcess(LLMServiceObj llmServiceObj);
    Task<ResultObj> SendInputAndGetResponse(LLMServiceObj serviceObj);
}

public class LLMService : ILLMService
{
    private ILogger _logger;
    private readonly ILLMProcessRunner _processRunner;
    private IRabbitRepo _rabbitRepo;

    private readonly ConcurrentDictionary<string, Session> _sessions = new ConcurrentDictionary<string, Session>();
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
            await _rabbitRepo.PublishAsync<LLMServiceObj>("llmServiceStarted", llmServiceObj);
        }
        catch (Exception e)
        {
            llmServiceObj.ResultMessage = e.Message;
            llmServiceObj.ResultSuccess = false;
        }

        if (!llmServiceObj.ResultSuccess)
        {
            llmServiceObj.LlmMessage = llmServiceObj.ResultMessage;
            await _rabbitRepo.PublishAsync<LLMServiceObj>("llmServiceMessage", llmServiceObj);
        }
        return llmServiceObj;
    }

    public LLMServiceObj RemoveProcess(LLMServiceObj llmServiceObj)
    {
        try
        {
            _processRunner.RemoveProcess(llmServiceObj.SessionId);
            _sessions[llmServiceObj.SessionId] = new Session();
            llmServiceObj.ResultMessage = " Success : LLMService Removed Session .";
            llmServiceObj.ResultSuccess = true;
        }
        catch (Exception e)
        {
            llmServiceObj.ResultMessage = e.Message;
            llmServiceObj.ResultSuccess = false;
        }


        return llmServiceObj;
    }


    public async Task<ResultObj> SendInputAndGetResponse(LLMServiceObj llmServiceObj)
    {
        var result = new ResultObj();

        if (llmServiceObj.SessionId == null && !_sessions.TryGetValue(llmServiceObj.SessionId, out var session))
        {
            result.Message = "Invalid session ID";
            result.Success = false;

        }
        else
        {
            try
            {
                await _processRunner.SendInputAndGetResponse(llmServiceObj.SessionId, llmServiceObj.UserInput, llmServiceObj.IsFunctionCallResponse);
                result.Message = " Processed UserInput :" + llmServiceObj.UserInput;
                result.Success = true;
            }
            catch (Exception e)
            {
                result.Message += $" Error : failed to send and process user input {e.Message}";
                result.Success = false;
            }
        }

        if (!result.Success)
        {
            llmServiceObj.LlmMessage = result.Message;
            await _rabbitRepo.PublishAsync<LLMServiceObj>("llmServiceMessage", llmServiceObj);
        }

        return result;
    }

    public void EndSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
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
        //Console.WriteLine(serviceObj.LlmMessage);
        await _rabbitRepo.PublishAsync<LLMServiceObj>("llmServiceMessage", serviceObj);
        //return Task.CompletedTask;
    }

    public async Task ProcessFunctionCall(LLMServiceObj serviceObj)
    {
        await _rabbitRepo.PublishAsync<LLMServiceObj>("llmServiceFunction", serviceObj);

    }

    public bool IsFunctionCallResponse(string input)
    {
        try
        {
            if (input == "") return false;
            FunctionCallData functionCallData = JsonSerializer.Deserialize<FunctionCallData>(input);
            return true;
        }
        catch (Exception ex)
        {
            //Console.WriteLine($"Error parsing JSON: {ex.Message}");
            return false;
        }
    }

    public bool IsFunctionCallResponseCL(string input)
    {
        try
        {
            if (input == "") return false;
            if (!input.StartsWith("<function>")) return false;

            return true;
        }
        catch (Exception ex)
        {
            //Console.WriteLine($"Error parsing JSON: {ex.Message}");
            return false;
        }
    }
}


public enum ResponseState { Initial, AwaitingInput, FunctionCallProcessed, Completed }

public class Session
{
    public List<string> Responses { get; } = new List<string>();
}