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
public class TokenBroadcaster
{
    private readonly ILLMResponseProcessor _responseProcessor;
    private readonly ILogger _logger;
    public event Func<object, string, Task> LineReceived;
    private CancellationTokenSource _cancellationTokenSource;
    private LLMServiceObj _inputServiceObj;
    private Task _currentBroadcastTask;

    public TokenBroadcaster(ILLMResponseProcessor responseProcessor, ILogger logger, LLMServiceObj inputServiceObj)
    {
        _responseProcessor = responseProcessor;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();
        _inputServiceObj = inputServiceObj;

    }


    public async Task ReInit(LLMServiceObj serviceObj)
    {
           _logger.LogInformation(" Cancel due to ReInit called ");
                
        await _cancellationTokenSource.CancelAsync();
       if (_currentBroadcastTask != null)
                await _currentBroadcastTask;
     
        _inputServiceObj = serviceObj;
    }
    public async Task BroadcastAsync(ProcessWrapper process, string sessionId)
    {
        _logger.LogWarning(" Start BroadcastAsyc() ");
        _currentBroadcastTask = Task.Run(async () =>
   {
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
                await ProcessLine(line);
               if (line == "\n")
               {
                   emptyLineCount++;
               }
             
               if (emptyLineCount == 2 || line == ">\n")
               {
                   //state = ResponseState.Completed;
                   _logger.LogInformation(" Cancel due to output end detected ");
                   _cancellationTokenSource.Cancel();
               }


               lineBuilder.Clear();
           }


           if (charRead == -1)
           {
               break;
           }// End of stream*/
       }
       _logger.LogInformation(" --> Finshed LLM Interaction ");

   });
        await _currentBroadcastTask;
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

    private async Task ProcessLine(string line)
    {
        LLMServiceObj responseServiceObj;
        string cleanLine = ParseInput(line);
        if (_responseProcessor.IsFunctionCallResponse(cleanLine))
        {
            _logger.LogInformation($" ProcessLLMOutput(call_func) -> {cleanLine}");
            responseServiceObj = new LLMServiceObj() { SessionId = _inputServiceObj.SessionId, UserInput = _inputServiceObj.UserInput };
            responseServiceObj.LlmMessage = "</functioncall>";
            await _responseProcessor.ProcessLLMOutput(responseServiceObj);
            responseServiceObj.LlmMessage = "";
            responseServiceObj.IsFunctionCall = true;
            responseServiceObj.JsonFunction = cleanLine;
            await _responseProcessor.ProcessFunctionCall(responseServiceObj);
        }

        responseServiceObj = new LLMServiceObj() { SessionId = _inputServiceObj.SessionId };
        responseServiceObj.LlmMessage = "<end-of-line>";
        await _responseProcessor.ProcessLLMOutput(responseServiceObj);

    }
    private string ParseInput(string input)
    {
        //_logger.LogInformation($" before -> {input} <-");
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
       // _logger.LogInformation($" after -> {newLine} <-");
        return newLine;
    }


}