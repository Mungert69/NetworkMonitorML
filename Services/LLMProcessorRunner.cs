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
        startInfo.Arguments = "-c 6000 -n 6000 -m /home/mahadeva/code/models/natural-functions.Q4_K_M.gguf  --prompt-cache /home/mahadeva/context.gguf --prompt-cache-ro  -f /home/mahadeva/initialPrompt.txt -ins --keep -1 --reverse-prompt \"User:\" --temp 0";
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
    private string ParseInput(string input)
    {
        _logger.LogInformation($" before -> {input} <-");
        if (input.Contains("FUNCTION RESPONSE")) return input;
        string newLine = string.Empty;
        int startIndex = input.IndexOf('{');
        if (startIndex != -1)
        {
            newLine = input.Substring(startIndex);
        }
        else
        {
            return newLine;
        }
        int lastClosingBraceIndex = newLine.LastIndexOf('}');
        if (lastClosingBraceIndex != -1)
        {
            newLine = newLine.Substring(0, lastClosingBraceIndex + 1); // Keep the last '}'
        }
        newLine = newLine.Replace("'", "");
        _logger.LogInformation($" after -> {newLine} <-");
        return newLine;
    }
    public async Task<string> SendInputAndGetResponse(LLMServiceObj serviceObj)
    {
        var responseServiceObj = new LLMServiceObj() { SessionId = serviceObj.SessionId, UserInput=serviceObj.UserInput };
        if (!_processes.TryGetValue(serviceObj.SessionId, out var process))
            throw new Exception("No process found for the given session");
        _logger.LogInformation($"  LLMService : SendInputAndGetResponse() :");
        if (process == null || process.HasExited)
        {
            throw new InvalidOperationException("LLM process is not running");
        }
        var state = ResponseState.Initial;
        int emptyLineCount = 0;
        // Create a cancellation token source
        var cancellationTokenSource = new CancellationTokenSource();
        // Create an instance of the TokenBroadcaster
        var tokenBroadcaster = new TokenBroadcaster(_responseProcessor, _logger);
        tokenBroadcaster.LineReceived += async (sender, line) =>
        {
            //if (state == ResponseState.FunctionCallProcessed) { cancellationTokenSource.Cancel(); }
            string cleanLine = ParseInput(line);
            if (_responseProcessor.IsFunctionCallResponse(cleanLine))
            {
                _logger.LogInformation($" ProcessLLMOutput(call_func) -> {cleanLine}");
                responseServiceObj = new LLMServiceObj() { SessionId = serviceObj.SessionId , UserInput=serviceObj.UserInput};
                responseServiceObj.LlmMessage = "</functioncall>";
                await _responseProcessor.ProcessLLMOutput(responseServiceObj);
                responseServiceObj.LlmMessage = "";
                responseServiceObj.IsFunctionCall = true;
                responseServiceObj.JsonFunction = cleanLine;
                await _responseProcessor.ProcessFunctionCall(responseServiceObj);
                state = ResponseState.FunctionCallProcessed;
                //cancellationTokenSource.Cancel();
            }
            if (line == "")
            {
                emptyLineCount++;
            }
            if (emptyLineCount == 2 || line==">")
            {
                state = ResponseState.Completed;
                cancellationTokenSource.Cancel();
            }
            responseServiceObj = new LLMServiceObj() { SessionId = serviceObj.SessionId };
            responseServiceObj.LlmMessage = "<end-of-line>";
            await _responseProcessor.ProcessLLMOutput(responseServiceObj);
        };
        try
        {
            // Acquire the semaphore (wait for the semaphore if it's not available)
            await _inputStreamSemaphore.WaitAsync();
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
            _inputStreamSemaphore.Release();
        }
        await tokenBroadcaster.BroadcastAsync(process, serviceObj.SessionId, cancellationTokenSource.Token);
        //cancellationTokenSource.Cancel();
        return string.Empty;
    }
}
public class ProcessWrapper
{
    private Process _process;
    public ProcessWrapper()
    {
        _process = new Process();
    }
    public ProcessWrapper(Process? process = null)
    {
        if (process == null) _process = new Process();
        else _process = process;
    }
    public virtual IStreamWriter StandardInput => new StreamWriterWrapper(_process.StandardInput);
    public virtual IStreamReader StandardOutput => new StreamReaderWrapper(_process.StandardOutput);
    public virtual ProcessStartInfo StartInfo => _process.StartInfo;
    public virtual bool StandardOutputEndOfStream => _process.StandardOutput.EndOfStream;
    public virtual bool HasExited => _process.HasExited;
    public virtual void Kill() => _process.Kill();
    public virtual void Dispose() => _process.Dispose();
    public virtual void Start()
    {
        _process.Start();
    }
    public virtual Task<string> StandardOutputReadLineAsync()
    {
        return _process.StandardOutput.ReadLineAsync();
    }
    public virtual Task StandardInputWriteLineAsync(string input)
    {
        return _process.StandardInput.WriteLineAsync(input);
    }
    public virtual Task StandardInputFlushAsync()
    {
        return _process.StandardInput.FlushAsync();
    }
    public virtual async Task<int> ReadAsync(byte[] buffer, int offset, int count)
    {
        return await _process.StandardOutput.BaseStream.ReadAsync(buffer, offset, count);
    }
    // Add any other methods or properties you need to mock
}
public interface IStreamReader
{
    Task<string> ReadLineAsync();
    Task<int> ReadAsync(byte[] buffer, int offset, int count);
    // Add other necessary methods from StreamReader
}
public interface IStreamWriter
{
    Task WriteLineAsync(string value);
    Task FlushAsync();
    // Add other necessary methods from StreamWriter
}
public class StreamReaderWrapper : IStreamReader
{
    private readonly StreamReader _innerStreamReader;
    public StreamReaderWrapper(StreamReader streamReader)
    {
        _innerStreamReader = streamReader;
    }
    public Task<string> ReadLineAsync()
    {
        return _innerStreamReader.ReadLineAsync();
    }
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count)
    {
        return await _innerStreamReader.BaseStream.ReadAsync(buffer, offset, count);
    }
    // Implement other methods from IStreamReader if needed
}
public class StreamWriterWrapper : IStreamWriter
{
    private readonly StreamWriter _innerStreamWriter;
    public StreamWriterWrapper(StreamWriter streamWriter)
    {
        _innerStreamWriter = streamWriter;
    }
    public Task WriteLineAsync(string value)
    {
        return _innerStreamWriter.WriteLineAsync(value);
    }
    public Task FlushAsync()
    {
        return _innerStreamWriter.FlushAsync();
    }
    // Implement other methods from IStreamWriter if needed
}