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
}

public class LLMProcessRunner : ILLMProcessRunner
{
    //private ProcessWrapper _llamaProcess;
    private readonly Dictionary<string, ProcessWrapper> _processes = new Dictionary<string, ProcessWrapper>();

    private ILogger _logger;
    private ILLMResponseProcessor _responseProcessor;

    public LLMProcessRunner(ILogger<LLMProcessRunner> logger, ILLMResponseProcessor responseProcessor)
    {
        _logger = logger;
        _responseProcessor = responseProcessor;
        //_llamaProcess = new ProcessWrapper();
        //SetStartInfo();

    }


    public void SetStartInfo(ProcessStartInfo startInfo, string modelPath)
    {
        startInfo.FileName = "/home/mahadeva/code/llama.cpp/build/bin/main";
        startInfo.Arguments = "-c 6000 -n 1000 -m /home/mahadeva/code/models/natural-functions.Q4_K_M.gguf  --prompt-cache /home/mahadeva/context.gguf --prompt-cache-ro  -f /home/mahadeva/initialPrompt.txt -ins --keep -1 --temp 0";
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
    private string RemoveAnsiEscapeSequences(string input)
    {
         _logger.LogInformation($" before -> {input} <-");
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
        // input=System.Text.RegularExpressions.Regex.Replace(input, @"\x1B\[([0-9]{1,2}(;[0-9]{1,2})?)?[m|K]", "");
        newLine = newLine.Replace("'", "");
         _logger.LogInformation($" after -> {newLine} <-");
        return newLine;
    }
    public async Task<string> SendInputAndGetResponse(LLMServiceObj serviceObj)
    {
        var responseServiceObj = new LLMServiceObj() { SessionId = serviceObj.SessionId };
        if (!_processes.TryGetValue(serviceObj.SessionId, out var process))
            throw new Exception("No process found for the given session");

        _logger.LogInformation($"  LLMService : SendInputAndGetResponse() :");
        if (process == null || process.HasExited)
        {
            throw new InvalidOperationException("LLM process is not running");
        }

        await process.StandardInput.WriteLineAsync(serviceObj.UserInput);
        await process.StandardInput.FlushAsync();

        var responseBuilder = new StringBuilder();
        responseServiceObj.LlmMessage = serviceObj.UserInput;
        await _responseProcessor.ProcessLLMOutput(responseServiceObj);
        _logger.LogInformation($" ProcessLLMOutput(user input) -> {serviceObj.UserInput}");
        string line;
        int emptyLineCount = 0;
        var state = ResponseState.Initial;

        while ((line = await process.StandardOutput.ReadLineAsync()) != null)
        {
           // _logger.LogInformation($" Process -> {line}");
            // build non prompt or json lines
           // if (!line.StartsWith("{")) responseBuilder.AppendLine(line);

            if (state == ResponseState.Initial )
            {
                // first line with user input is ignored ?
                state = ResponseState.AwaitingInput;
            }
            else if (state == ResponseState.AwaitingInput)
            {
                string str;
                string cleanLine = RemoveAnsiEscapeSequences(line);
                if (_responseProcessor.IsFunctionCallResponse(cleanLine))
                {
                    // call function send user llm output
                    responseBuilder.Append($"Calling Function : {cleanLine}");
                    //str = responseBuilder.ToString();
                    responseServiceObj = new LLMServiceObj() { SessionId = serviceObj.SessionId };
                    responseServiceObj.LlmMessage = line;
                    await _responseProcessor.ProcessLLMOutput(responseServiceObj);
                    _logger.LogInformation($" ProcessLLMOutput(call_func) -> {cleanLine}");
                    responseBuilder.Clear();
                    responseServiceObj = new LLMServiceObj() { SessionId = serviceObj.SessionId };
                    responseServiceObj.IsFunctionCall = true;

                    responseServiceObj.JsonFunction = cleanLine;

                    await _responseProcessor.ProcessFunctionCall(responseServiceObj);
                    state = ResponseState.FunctionCallProcessed;
                    break;
                }

                // back to prompt finshed.
                //str = responseBuilder.ToString();
                responseServiceObj = new LLMServiceObj() { SessionId = serviceObj.SessionId };
                responseServiceObj.LlmMessage = line;
                await _responseProcessor.ProcessLLMOutput(responseServiceObj);
                responseBuilder.Clear();
                //state = ResponseState.AwaitingInput;
                if (line == "") emptyLineCount++;
                if ( emptyLineCount==2)
                {
                    state = ResponseState.Completed;
                    break;
                }


            }
            else if (state == ResponseState.FunctionCallProcessed)
            {
                // after function call
                var str = responseBuilder.ToString();
                responseServiceObj = new LLMServiceObj() { SessionId = serviceObj.SessionId };
                responseServiceObj.LlmMessage = str;
                await _responseProcessor.ProcessLLMOutput(responseServiceObj);
                _logger.LogInformation($" ProcessLLMOutput(after_func_call) -> {str}");
                responseBuilder.Clear();
                state = ResponseState.Completed;
                break;
            }
        }
        _logger.LogInformation(" --> Finshed LLM Interaction ");
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

    public virtual bool HasExited => _process.HasExited;

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

    // Add any other methods or properties you need to mock
}

public interface IStreamReader
{
    Task<string> ReadLineAsync();
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

