using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
namespace NetworkMonitor.Service.Services;
public class LLMService
{
    enum ResponseState { Initial, AwaitingInput, FunctionCallProcessed } 

    private Process _llamaProcess; // Member to hold the LLM process instance

    public async Task StartProcess(string modelPath, /* other options */)
    {
        // ... same as before ...
        // Create and configure the process in the constructor
        _llamaProcess = new Process();
        _llamaProcess.StartInfo.FileName = "~/code/llama.cpp/build/bin/main";
        _llamaProcess.StartInfo.Arguments = "-c 6000 -m ~/code/models/natural-functions.Q8_0.gguf -ins --prompt-cache context.gguf --prompt-cache-ro --keep -1 -f initialPrompt.txt";

        _llamaProcess.StartInfo.UseShellExecute = false;
        _llamaProcess.StartInfo.RedirectStandardInput = true;
        _llamaProcess.StartInfo.RedirectStandardOutput = true;
        _llamaProcess.StartInfo.CreateNoWindow = true;

        _llamaProcess.Start();

        // Wait for the "ready" signal
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

   
   
public async Task<string> SendInputAndGetResponse(string userInput)
{
    if (_llamaProcess == null || _llamaProcess.HasExited)
    {
        throw new InvalidOperationException("LLM process is not running");
    }

    // Send input to the LLM process
    await _llamaProcess.StandardInput.WriteLineAsync(userInput);
    await _llamaProcess.StandardInput.FlushAsync();

    var responseBuilder = new StringBuilder();
    string line;
    var state = ResponseState.Initial;

    while ((line = await _llamaProcess.StandardOutput.ReadLineAsync()) != null)
    {
        responseBuilder.AppendLine(line);

        if (state == ResponseState.Initial && line.StartsWith(">"))
        {
            state = ResponseState.AwaitingInput;
        }
        else if (state == ResponseState.AwaitingInput) 
        {
            if (IsFunctionCallResponse(line))
            {
                await ProcessFunctionCall(line);
                state = ResponseState.FunctionCallProcessed;
            }
            else if (line.StartsWith(">")) // End of non-function-call response
            {
                await ProcessLLMOutput(responseBuilder.ToString());
                responseBuilder.Clear();  
                state = ResponseState.AwaitingInput;
            } 
            // else (accumulate lines while waiting for response end)
        }
        else if (state == ResponseState.FunctionCallProcessed)
        {
            await ProcessLLMOutput(responseBuilder.ToString());
            responseBuilder.Clear();
            state = ResponseState.AwaitingInput; 
        }
    }

    return ""; // Response was already processed
}

private async Task ProcessLLMOutput(string output)
{
    //  You might want to display this output directly to the user 
    Console.WriteLine(output); 
}

  
private async Task ProcessLLMResponse(string completeResponse)
{
    // Check for JSON function call instructions (updated logic might be needed)
    if (IsFunctionCallResponse(completeResponse)) 
    {
        await ProcessFunctionCall(completeResponse);
    }
    // ... other logic to handle non-function call responses
}

    private bool IsFunctionCallResponse(string input)
    {
        try
        {
            var jsonElement= JsonDocument.Parse(input).RootElement;
            return jsonElement.TryGetProperty("name", out _);
        }
        catch (JsonException)
        {
            return false; // Not a valid JSON function call instruction
        }
    }

    private async Task ProcessFunctionCall(string jsonStr)
    {
        var functionCallData = JsonSerializer.Deserialize<FunctionCallData>(jsonStr);

        // Implement logic to execute the function based on functionCallData.functionName
        // You might use reflection or a switch-case statement for this.
        switch (functionCallData.functionName)
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
                Console.WriteLine("Unknown function: " + functionCallData.functionName); 
                break; 
        }
    }

    // Placeholder function implementations - You'll need to replace these!
    private async Task CallAddHostFunction(Dictionary<string, string> parameters)
    { 
        // Implement your actual host adding logic with parameters
    }

    private async Task CallEditHostFunction(Dictionary<string, string> parameters) 
    { 
        // Implement your actual host editing logic with parameters
    }

    private async Task CallGetHostDataFunction(Dictionary<string, string> parameters) 
    { 
        // Implement your actual host data retrieval logic with parameters
    }
}

// Helper class to represent the deserialized JSON
public class FunctionCallData
{
    public string functionName { get; set; }
    public Dictionary<string, string> parameters { get; set; } 
}

