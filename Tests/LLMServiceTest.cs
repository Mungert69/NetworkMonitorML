using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Moq;
using Xunit;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Objects.Repository;

namespace NetworkMonitor.ML.Services;

public class LLMProcessRunnerTests
{

    private readonly Mock<ILogger<LLMService>> _loggerLLMServiceMock;
      private readonly Mock<ILogger<LLMResponseProcessor>> _loggerLLMResponseProcessorMock;
       private readonly Mock<ILogger<LLMProcessRunner>> _loggerLLMProcessRunnerMock;
               private readonly Mock<IRabbitRepo> _rabbitRepoMock;
       public LLMProcessRunnerTests()
        {
            _loggerLLMServiceMock = new Mock<ILogger<LLMService>>();

        _loggerLLMProcessRunnerMock = new Mock<ILogger<LLMProcessRunner>>();
        _loggerLLMResponseProcessorMock = new Mock<ILogger<LLMResponseProcessor>>();
                    _rabbitRepoMock = new Mock<IRabbitRepo>();
        }

    [Fact]
    public async Task StartProcess_ShouldStartProcessAndWaitForReadySignal()
    {
        // Arrange
        var mockProcessWrapper = new Mock<ProcessWrapper>();
        mockProcessWrapper.Setup(p => p.StandardOutput.ReadLineAsync())
               .Returns(() =>
               {
                   string line = "<|im_end|>";
                   return Task.FromResult(line);
               });
                var mockResponseProcessor = new Mock<ILLMResponseProcessor>();
        mockProcessWrapper.Setup(p => p.Start());
        var processRunner = new LLMProcessRunner(_loggerLLMProcessRunnerMock.Object,mockResponseProcessor.Object);


        // Act
        await processRunner.StartProcess("test","path/to/model",mockProcessWrapper.Object);

        // Assert
        mockProcessWrapper.Verify(p => p.Start(), Times.Once);
        mockProcessWrapper.Verify(p => p.StandardOutput.ReadLineAsync(), Times.Exactly(1));
    }
    [Fact]
    public async Task SendInputAndGetResponse_ShouldSendInputAndProcessOutput()
    {
         
        // Arrange
        var mockProcessWrapper = new Mock<ProcessWrapper>();
        mockProcessWrapper.Setup(p => p.StandardInput.WriteLineAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mockProcessWrapper.Setup(p => p.StandardInput.FlushAsync())
            .Returns(Task.CompletedTask);

        var outputLines = new[]
        {
        "<|im_end|>",
        "> Add Host 192.168.1.1",
        "{\"name\":\"AddHostGPTDefault\",\"parameters\":{\"host\":\"192.168.1.1\"}}",
        "\n",
        "\n",
        ">"
    };

        var outputEnumerator = outputLines.GetEnumerator();
        var shouldContinue = true; // Introduce a flag
                                   //outputEnumerator.MoveNext();
        mockProcessWrapper.Setup(p => p.StandardOutput.ReadLineAsync())
     .Returns(async () =>
     {
         if (outputEnumerator.MoveNext())
             return outputEnumerator.Current as string;
         else
         {

             return null;
         }
     });
        var serviceObj = new LLMServiceObj() { SessionId = "test", UserInput="> Add Host 192.168.1.1" };
        var mockResponseProcessor = new Mock<ILLMResponseProcessor>();
        mockResponseProcessor.Setup(p => p.IsFunctionCallResponse(It.IsAny<string>()))
            .Returns<string>(input => input.StartsWith("{"));
        mockResponseProcessor.Setup(p => p.ProcessFunctionCall(It.IsAny<LLMServiceObj>()))
            .Returns(Task.CompletedTask);
        mockResponseProcessor.Setup(p => p.ProcessLLMOutput(It.IsAny<LLMServiceObj>()))
            .Returns(Task.CompletedTask);

        var processRunner = new LLMProcessRunner(_loggerLLMProcessRunnerMock.Object,mockResponseProcessor.Object);
       await processRunner.StartProcess("test","path/to/model",mockProcessWrapper.Object);
        // Act
        await processRunner.SendInputAndGetResponse(serviceObj.SessionId,serviceObj.UserInput);

         // Assert
    mockProcessWrapper.Verify(p => p.StandardInput.WriteLineAsync("> Add Host 192.168.1.1"), Times.Once);
    mockProcessWrapper.Verify(p => p.StandardInput.FlushAsync(), Times.Once);

    mockResponseProcessor.Verify(
        p => p.ProcessLLMOutput(It.Is<LLMServiceObj>(obj => obj.SessionId == "test" && obj.LlmMessage == "> Add Host 192.168.1.1")),
        Times.Once);

    mockResponseProcessor.Verify(
        p => p.ProcessFunctionCall(It.Is<LLMServiceObj>(obj => obj.SessionId == "test" && obj.IsFunctionCall && obj.JsonFunction == "{\"name\":\"AddHostGPTDefault\",\"parameters\":{\"host\":\"192.168.1.1\"}}")),
        Times.Once);

    mockResponseProcessor.Verify(
        p => p.ProcessLLMOutput(It.Is<LLMServiceObj>(obj => obj.SessionId == "test" && obj.LlmMessage == "> Add Host 192.168.1.1\nCalling Function : {\"name\":\"AddHostGPTDefault\",\"parameters\":{\"host\":\"192.168.1.1\"}}")),
        Times.Once);   }
}
public class LLMResponseProcessorTests
{
                   private readonly Mock<IRabbitRepo> _rabbitRepoMock;

                    public LLMResponseProcessorTests()
        {
           
                    _rabbitRepoMock = new Mock<IRabbitRepo>();
        }

    [Fact]
    public void IsFunctionCallResponse_ShouldReturnTrueForValidJson()
    {
        // Arrange
        var responseProcessor = new LLMResponseProcessor( _rabbitRepoMock.Object);
        var jsonInput = "{\"name\":\"SomeFunction\",\"parameters\":{}}";

        // Act
        var result = responseProcessor.IsFunctionCallResponse(jsonInput);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsFunctionCallResponse_ShouldReturnFalseForInvalidJson()
    {
        // Arrange
        var responseProcessor = new LLMResponseProcessor(_rabbitRepoMock.Object);
        var invalidInput = "This is not JSON";

        // Act
        var result = responseProcessor.IsFunctionCallResponse(invalidInput);

        // Assert
        Assert.False(result);
    }
}


