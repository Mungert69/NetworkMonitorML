using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;




namespace NetworkMonitor.ML.Services;
// LLMService.cs
public interface ILLMService
{
    Task StartProcess(string modelPath);
    Task<string> SendInputAndGetResponse(string userInput);
}

public class LLMService : ILLMService
{
    private readonly ILLMProcessRunner _processRunner;
    private readonly ILLMResponseProcessor _responseProcessor;

    public LLMService(ILLMProcessRunner processRunner, ILLMResponseProcessor responseProcessor)
    {
        _processRunner = processRunner;
        _responseProcessor = responseProcessor;
    }

    public async Task StartProcess(string modelPath)
    {
        await _processRunner.StartProcess(modelPath);
    }

    public async Task<string> SendInputAndGetResponse(string userInput)
    {
        return await _processRunner.SendInputAndGetResponse(userInput, _responseProcessor);
    }
}

// LLMProcessRunner.cs
public interface ILLMProcessRunner
{
    Task StartProcess(string modelPath);
    Task<string> SendInputAndGetResponse(string userInput, ILLMResponseProcessor responseProcessor);
}

public class LLMProcessRunner : ILLMProcessRunner
{
    private ProcessWrapper _llamaProcess;

    public LLMProcessRunner(ProcessWrapper? process, bool setStartInfo = true)
    {
        if (process == null) _llamaProcess = new ProcessWrapper();
        else _llamaProcess = process;
        if (setStartInfo) SetStartInfo();
    }

    public void SetStartInfo()
    {
        _llamaProcess.StartInfo.FileName = "~/code/llama.cpp/build/bin/main";
        _llamaProcess.StartInfo.Arguments = "-c 6000  -m ~/code/models/natural-functions.Q4_K_M.gguf  --prompt-cache context.gguf --prompt-cache-ro  -f initialPrompt.txt --color -r \"User:\" --in-prefix \" \" -ins --keep -1 --temp 0";
        _llamaProcess.StartInfo.UseShellExecute = false;
        _llamaProcess.StartInfo.RedirectStandardInput = true;
        _llamaProcess.StartInfo.RedirectStandardOutput = true;
        _llamaProcess.StartInfo.CreateNoWindow = true;
    }
    public async Task StartProcess(string modelPath)
    {
        if (_llamaProcess == null)
        {
            throw new InvalidOperationException("LLM process is not initialized");
        }
        _llamaProcess.Start();
        await WaitForReadySignal();
    }

    private async Task WaitForReadySignal()
    {
        bool isReady = false;
        string line;
        while ((line = await _llamaProcess.StandardOutput.ReadLineAsync()) != null)
        {
            if (line.StartsWith(">"))
            {
                isReady = true;
                break;
            }
        }

        if (!isReady)
        {
            throw new Exception("LLM process failed to indicate readiness");
        }
    }

    public async Task<string> SendInputAndGetResponse(string userInput, ILLMResponseProcessor responseProcessor)
    {
        if (_llamaProcess == null || _llamaProcess.HasExited)
        {
            throw new InvalidOperationException("LLM process is not running");
        }

        await _llamaProcess.StandardInput.WriteLineAsync(userInput);
        await _llamaProcess.StandardInput.FlushAsync();

        var responseBuilder = new StringBuilder();
        await responseProcessor.ProcessLLMOutput(userInput);
        string line;
        var state = ResponseState.Initial;

        while ((line = await _llamaProcess.StandardOutput.ReadLineAsync()) != null)
        {
            // build non prompt or json lines
            if (!line.StartsWith("{")) responseBuilder.AppendLine(line);

            if (state == ResponseState.Initial && line.StartsWith(">"))
            {
                // first line with user input is ignored ?
                state = ResponseState.AwaitingInput;
            }
            else if (state == ResponseState.AwaitingInput)
            {
                if (responseProcessor.IsFunctionCallResponse(line))
                {
                    // call function send user llm output
                    responseBuilder.Append($"Calling Function : {line}");
                    var str = responseBuilder.ToString();
                    await responseProcessor.ProcessLLMOutput(str);
                    responseBuilder.Clear();
                    await responseProcessor.ProcessFunctionCall(line);
                    state = ResponseState.FunctionCallProcessed;
                }
                else if (line.StartsWith(">"))
                {
                    // back to prompt finshed.
                    var str = responseBuilder.ToString();
                    await responseProcessor.ProcessLLMOutput(str);
                    responseBuilder.Clear();
                    state = ResponseState.Completed;
                }
            }
            else if (state == ResponseState.FunctionCallProcessed)
            {
                // after function call
                var str = responseBuilder.ToString();
                await responseProcessor.ProcessLLMOutput(str);
                responseBuilder.Clear();
                state = ResponseState.AwaitingInput;
            }
        }

        return string.Empty;
    }
}

// LLMResponseProcessor.cs
public interface ILLMResponseProcessor
{
    Task ProcessLLMOutput(string output);
    Task ProcessFunctionCall(string jsonStr);
    bool IsFunctionCallResponse(string input);
}

public class LLMResponseProcessor : ILLMResponseProcessor
{
    private readonly IFunctionExecutor _functionExecutor;

    public LLMResponseProcessor(IFunctionExecutor functionExecutor)
    {
        _functionExecutor = functionExecutor;
    }

    public Task ProcessLLMOutput(string output)
    {
        Console.WriteLine(output);
        return Task.CompletedTask;
    }

    public async Task ProcessFunctionCall(string jsonStr)
    {
        var functionCallData = JsonSerializer.Deserialize<FunctionCallData>(jsonStr);
        await _functionExecutor.ExecuteFunction(functionCallData);
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

// FunctionExecutor.cs
public interface IFunctionExecutor
{
    Task ExecuteFunction(FunctionCallData functionCallData);
}

public class FunctionExecutor : IFunctionExecutor
{
    public async Task ExecuteFunction(FunctionCallData functionCallData)
    {
        switch (functionCallData.name)
        {
            case "AddHostGPTDefault":
                await CallAddHostFunction(functionCallData.parameters);
                break;
            case "EditHostGPTDefault":
                await CallEditHostFunction(functionCallData.parameters);
                break;
            case "GetHostDataByHostAddressDefault":
                await CallGetHostDataFunction(functionCallData.parameters);
                break;
            default:
                Console.WriteLine("Unknown function: " + functionCallData.name);
                break;
        }
    }

    private async Task CallAddHostFunction(Dictionary<string, string> parameters)
    {
        Console.WriteLine("Add host function called with parameters:");
        foreach (var kvp in parameters)
        {
            Console.WriteLine($"{kvp.Key}: {kvp.Value}");
        }
    }

    private async Task CallEditHostFunction(Dictionary<string, string> parameters)
    {
        Console.WriteLine("Edit host function called with parameters:");
        foreach (var kvp in parameters)
        {
            Console.WriteLine($"{kvp.Key}: {kvp.Value}");
        }
    }

    private async Task CallGetHostDataFunction(Dictionary<string, string> parameters)
    {
        Console.WriteLine("Get host data function called with parameters:");
        foreach (var kvp in parameters)
        {
            Console.WriteLine($"{kvp.Key}: {kvp.Value}");
        }
    }
}

// Helper class to represent the deserialized JSON
public class FunctionCallData
{
    public string name { get; set; }
    public Dictionary<string, string> parameters { get; set; }
}

public enum ResponseState { Initial, AwaitingInput, FunctionCallProcessed, Completed }

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

