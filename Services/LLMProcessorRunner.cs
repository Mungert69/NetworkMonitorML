using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects.ServiceMessage;
namespace NetworkMonitor.ML.Services;
// LLMProcessRunner.cs
public interface ILLMProcessRunner
{
    Task StartProcess(string sessionId, string modelPath, ProcessWrapper? testProcess = null);
    Task<string> SendInputAndGetResponse(LLMServiceObj serviceObj);
    void RemoveProcess(string sessionId);
}
public class LLMProcessRunner : ILLMProcessRunner
{
    //private ProcessWrapper _llamaProcess;
    private readonly Dictionary<string, ProcessWrapper> _processes = new Dictionary<string, ProcessWrapper>();
    private readonly Dictionary<string, TokenBroadcaster> _tokenBroadcasters = new Dictionary<string, TokenBroadcaster>();

    private ILogger _logger;
    private ILLMResponseProcessor _responseProcessor;
    private readonly SemaphoreSlim _inputStreamSemaphore = new SemaphoreSlim(1, 1);
    public LLMProcessRunner(ILogger<LLMProcessRunner> logger, ILLMResponseProcessor responseProcessor)
    {
        _logger = logger;
        _responseProcessor = responseProcessor;
    }
    public void SetStartInfo(ProcessStartInfo startInfo, string modelPath)
    {
        startInfo.FileName = "/home/mahadeva/code/llama.cpp/build/bin/main";
        startInfo.Arguments = "-c 6000 -n 6000 -m /home/mahadeva/code/models/natural-functions.Q4_K_M.gguf  --prompt-cache /home/mahadeva/context.gguf --prompt-cache-ro  -f /home/mahadeva/initialPrompt.txt -ins --keep -1 --temp 0";
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.CreateNoWindow = true;
    }
    public async Task StartProcess(string sessionId, string modelPath, ProcessWrapper? testProcess = null)
    {
        if (_processes.ContainsKey(sessionId))
            throw new Exception("Process already running for this session");
        _logger.LogInformation($" LLM Service : Start Process for sessionsId {sessionId}");
        ProcessWrapper process;
        if (testProcess == null)
        {
            process = new ProcessWrapper();
            SetStartInfo(process.StartInfo, modelPath);
        }
        else
        {
            process = testProcess;
        }
        process.Start();
        await WaitForReadySignal(process);
        _processes[sessionId] = process;
        _logger.LogInformation($"LLM process started for session {sessionId}");
    }
    public void RemoveProcess(string sessionId)
    {
        if (!_processes.TryGetValue(sessionId, out var process))
            throw new Exception("Process is not running for this session");
        _logger.LogInformation($" LLM Service : Remove Process for sessionsId {sessionId}");
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        finally
        {
            // Always dispose of the process object
            process.Dispose();
        }
        _processes.Remove(sessionId);
        _logger.LogInformation($"LLM process removed for session {sessionId}");
    }
    private async Task WaitForReadySignal(ProcessWrapper process)
    {
        bool isReady = false;
        string line;
        //await Task.Delay(10000);
        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(TimeSpan.FromMinutes(1)); // Timeout after one minute
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            line = await process.StandardOutput.ReadLineAsync();
            if (line.StartsWith("<</SYS>>[/INST]"))
            {
                isReady = true;
                break;
            }
        }
        if (!isReady)
        {
            throw new Exception("LLM process failed to indicate readiness");
        }
        _logger.LogInformation($" LLMService Process Started ");
    }
    public async Task<string> SendInputAndGetResponse(LLMServiceObj serviceObj)
    {
        _logger.LogInformation($"  LLMService : SendInputAndGetResponse() :");

        if (!_processes.TryGetValue(serviceObj.SessionId, out var process))
            throw new Exception("No process found for the given session");

        if (process == null || process.HasExited)
        {
            throw new InvalidOperationException("LLM process is not running");
        }

        TokenBroadcaster tokenBroadcaster;
        if (_tokenBroadcasters.TryGetValue(serviceObj.SessionId, out tokenBroadcaster))
        {
            await tokenBroadcaster.ReInit(serviceObj);
        }
        else
        {
            tokenBroadcaster = new TokenBroadcaster(_responseProcessor, _logger, serviceObj);
        }
        try
        {
            // Acquire the semaphore (wait for the semaphore if it's not available)
            //_logger.LogInformation($" Wating for Semiphore..");
       
            //await _inputStreamSemaphore.WaitAsync();
            await process.StandardInput.WriteLineAsync(serviceObj.UserInput);
            await process.StandardInput.FlushAsync();
            _logger.LogInformation($" ProcessLLMOutput(user input) -> {serviceObj.UserInput}");
        }
        catch
        {
            throw;
        }
        finally
        {
            // Release the semaphore
            //_inputStreamSemaphore.Release();
        }
        await tokenBroadcaster.BroadcastAsync(process, serviceObj.SessionId);
        //cancellationTokenSource.Cancel();
        return string.Empty;
    }
}

