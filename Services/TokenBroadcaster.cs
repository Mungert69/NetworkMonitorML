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
using NetworkMonitor.Objects;

namespace NetworkMonitor.ML.Services;
public class TokenBroadcaster
{
    private readonly ILLMResponseProcessor _responseProcessor;
    private readonly ILogger _logger;
    public event Func<object, string, Task> LineReceived;
    private CancellationTokenSource _cancellationTokenSource;


    public TokenBroadcaster(ILLMResponseProcessor responseProcessor, ILogger logger)
    {
        _responseProcessor = responseProcessor;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();

    }


    public async Task ReInit(string sessionId)
    {
        _logger.LogInformation(" Cancel due to ReInit called ");

        await _cancellationTokenSource.CancelAsync();

    }
    public async Task BroadcastAsync(ProcessWrapper process, string sessionId, string userInput, bool isFunctionCallResponse)
    {
        _logger.LogWarning(" Start BroadcastAsyc() ");
        var lineBuilder = new StringBuilder();
        var tokenBuilder = new StringBuilder();
        int emptyLineCount = 0;
        var cancellationToken = _cancellationTokenSource.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] buffer = new byte[1]; // Choose an appropriate buffer size
                                         // int charRead = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
            int charRead = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
            string textChunk = Encoding.UTF8.GetString(buffer, 0, charRead);
            lineBuilder.Append(textChunk);

            // Console.WriteLine($"Bytes read: {BitConverter.ToString(buffer, 0, charRead)}");

            char currentChar = (char)charRead;

            //lineBuilder.Append(currentChar);
            tokenBuilder.Append(textChunk);
            //Console.WriteLine(lineBuilder.ToString());
            if (IsTokenComplete(tokenBuilder))
            {
                string token = tokenBuilder.ToString();
                tokenBuilder.Clear();

                var serviceObj = new LLMServiceObj { SessionId = sessionId, LlmMessage = token };
                await _responseProcessor.ProcessLLMOutput(serviceObj);
            }

            if (IsLineComplete(lineBuilder))
            {
                string line = lineBuilder.ToString();
                _logger.LogInformation($"sessionID={sessionId} line is =>{line}<=");
                await ProcessLine(line, sessionId, userInput, isFunctionCallResponse);
                if (line == "\n")
                {
                    emptyLineCount++;
                }

                if (emptyLineCount == 1 || line == "\n>")
                {
                    //state = ResponseState.Completed;
                    _logger.LogInformation(" Cancel due to output end detected ");
                    _cancellationTokenSource.Cancel();
                }


                lineBuilder.Clear();
            }



        }
        _logger.LogInformation(" --> Finshed LLM Interaction ");


        //await _currentBroadcastTask;
    }
    private bool IsLineComplete(StringBuilder lineBuilder)
    {
        return lineBuilder.ToString().EndsWith("\n");
    }


    private bool IsTokenComplete(StringBuilder tokenBuilder)
    {
        string token = tokenBuilder.ToString();
        if (token.Length > 0 && char.IsWhiteSpace(token[^1])) return true;

        // Check for whitespace characters that indicate token boundaries
        return false;
    }

    private async Task ProcessLine(string line, string sessionId, string userInput, bool isFunctionCallResponse)
    {
        LLMServiceObj responseServiceObj = new LLMServiceObj() { SessionId = sessionId };



        if (isFunctionCallResponse)
        {
            responseServiceObj.LlmMessage = "</functioncall-complete>";
            await _responseProcessor.ProcessLLMOutput(responseServiceObj);
        }
        else
        {
            string jsonLine = ParseInputForJson(line);
            //string cleanLine = line;
            if (line != jsonLine )
            {
                _logger.LogInformation($" ProcessLLMOutput(call_func) -> {jsonLine}");
                responseServiceObj = new LLMServiceObj() { SessionId = sessionId, UserInput = userInput };
                responseServiceObj.LlmMessage = "</functioncall>";
                await _responseProcessor.ProcessLLMOutput(responseServiceObj);
                responseServiceObj.LlmMessage = "";
                responseServiceObj.IsFunctionCall = true;
                responseServiceObj.JsonFunction = jsonLine;
                //responseServiceObj.JsonFunction = CallFuncJson(cleanLine);
                await _responseProcessor.ProcessFunctionCall(responseServiceObj);
            }


        }



        responseServiceObj.LlmMessage = "<end-of-line>";
        await _responseProcessor.ProcessLLMOutput(responseServiceObj);

    }
    public string CallFuncJson(string input)
    {
        string callFuncJson = "";
        string funcName = "addHost";
        int startIndex = input.IndexOf('{');
        int lastClosingBraceIndex = input.LastIndexOf('}');
        string json = "";
        if (startIndex != -1)
        {
            json = input.Substring(startIndex, lastClosingBraceIndex + 1);
        }
        callFuncJson = "{ \"name\" : \"" + funcName + "\" \"arguments\" : \"" + json + "\"}";
        return callFuncJson;

    }
    private string ParseInputForJson(string input)
    {
        if (input.Contains("FUNCTION RESPONSE:")) return input;
        string newLine = string.Empty;
        // bool foundStart = false;
        bool foundEnd = false;
        int startIndex = input.IndexOf('{');

        // If '{' is not found or is too far into the input, return the original input
        if (startIndex == -1 || startIndex > 20)
        {
            return input;
        }

        newLine = input.Substring(startIndex);

        int lastClosingBraceIndex = newLine.LastIndexOf('}');
        if (lastClosingBraceIndex != -1)
        {
            newLine = newLine.Substring(0, lastClosingBraceIndex + 1);
            foundEnd = true;
        }
        if (foundEnd) return JsonSanitizer.SanitizeJson(newLine);
        else return input;
    }


}