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
    public event EventHandler<string> LineReceived;
    public TokenBroadcaster(ILLMResponseProcessor responseProcessor)
    {
        _responseProcessor = responseProcessor;
    }

    public async Task BroadcastAsync(ProcessWrapper process, string sessionId, CancellationToken cancellationToken)
    {
        var lineBuilder = new StringBuilder();
        var tokenBuilder = new StringBuilder();

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
            Console.WriteLine(lineBuilder.ToString());

            if (IsLineComplete(lineBuilder))
            {
                string line = lineBuilder.ToString().Trim();
                lineBuilder.Clear();

                LineReceived?.Invoke(this, line);
            }

            if (IsTokenComplete(lineBuilder))
            {
                string token = tokenBuilder.ToString();
                tokenBuilder.Clear();

                var serviceObj = new LLMServiceObj { SessionId = sessionId, LlmMessage = token };
                await _responseProcessor.ProcessLLMOutput(serviceObj);
            }
            if (charRead == -1)
            {
                break;
            }// End of stream
        }
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

}