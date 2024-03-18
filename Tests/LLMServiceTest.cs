using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Moq;
using Xunit;
namespace NetworkMonitor.ML.Services;

public class LLMProcessRunnerTests
{

    private readonly Mock<ILogger<LLMService>> _loggerLLMServiceMock;
     private readonly Mock<ILogger<FunctionExecutor>> _loggerFunctionExecutorMock;
      private readonly Mock<ILogger<LLMResponseProcessor>> _loggerLLMResponseProcessorMock;
       private readonly Mock<ILogger<LLMProcessRunner>> _loggerLLMProcessRunnerMock;
       public LLMProcessRunnerTests()
        {
            _loggerLLMServiceMock = new Mock<ILogger<LLMService>>();
        _loggerFunctionExecutorMock = new Mock<ILogger<FunctionExecutor>>();
        _loggerLLMProcessRunnerMock = new Mock<ILogger<LLMProcessRunner>>();
        _loggerLLMResponseProcessorMock = new Mock<ILogger<LLMResponseProcessor>>();
        }

    [Fact]
    public async Task StartProcess_ShouldStartProcessAndWaitForReadySignal()
    {
        // Arrange
        var mockProcessWrapper = new Mock<ProcessWrapper>();
        mockProcessWrapper.Setup(p => p.StandardOutput.ReadLineAsync())
               .Returns(() =>
               {
                   string line = ">";
                   return Task.FromResult(line);
               });
                var mockResponseProcessor = new Mock<ILLMResponseProcessor>();
        mockProcessWrapper.Setup(p => p.Start());
        var processRunner = new LLMProcessRunner(_loggerLLMProcessRunnerMock.Object,mockResponseProcessor.Object,mockProcessWrapper.Object, false);


        // Act
        await processRunner.StartProcess("path/to/model");

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

        var mockResponseProcessor = new Mock<ILLMResponseProcessor>();
        mockResponseProcessor.Setup(p => p.IsFunctionCallResponse(It.IsAny<string>()))
            .Returns<string>(input => input.StartsWith("{"));
        mockResponseProcessor.Setup(p => p.ProcessFunctionCall(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mockResponseProcessor.Setup(p => p.ProcessLLMOutput(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var processRunner = new LLMProcessRunner(_loggerLLMProcessRunnerMock.Object,mockResponseProcessor.Object,mockProcessWrapper.Object, false);

        // Act
        await processRunner.SendInputAndGetResponse("> Add Host 192.168.1.1");

        // Assert
        mockProcessWrapper.Verify(p => p.StandardInput.WriteLineAsync("> Add Host 192.168.1.1"), Times.Once);
        mockProcessWrapper.Verify(p => p.StandardInput.FlushAsync(), Times.Once);
        mockResponseProcessor.Verify(p => p.ProcessLLMOutput("> Add Host 192.168.1.1"), Times.Once);
        mockResponseProcessor.Verify(p => p.ProcessFunctionCall("{\"name\":\"AddHostGPTDefault\",\"parameters\":{\"host\":\"192.168.1.1\"}}"), Times.Once);
        mockResponseProcessor.Verify(p => p.ProcessLLMOutput("> Add Host 192.168.1.1\nCalling Function : {\"name\":\"AddHostGPTDefault\",\"parameters\":{\"host\":\"192.168.1.1\"}}"), Times.Once);
    }
}
public class LLMResponseProcessorTests
{
    [Fact]
    public void IsFunctionCallResponse_ShouldReturnTrueForValidJson()
    {
        // Arrange
        var responseProcessor = new LLMResponseProcessor(null);
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
        var responseProcessor = new LLMResponseProcessor(null);
        var invalidInput = "This is not JSON";

        // Act
        var result = responseProcessor.IsFunctionCallResponse(invalidInput);

        // Assert
        Assert.False(result);
    }
}

public class FunctionExecutorTests
{
    [Fact]
    public async Task ExecuteFunction_ShouldCallAddHostFunction()
    {
        // Arrange
        var executor = new FunctionExecutor();
        var functionCallData = new FunctionCallData
        {
            name = "AddHostGPTDefault",
            parameters = new Dictionary<string, string>
            {
                { "host", "192.168.1.1" },
                { "description", "Test host" }
            }
        };

        // Act
        await executor.ExecuteFunction(functionCallData);

        // Assert (verify that the console output contains the expected parameters)
        var output = GetConsoleOutput();
        Assert.Contains("Add host function called with parameters:", output);
        Assert.Contains("host: 192.168.1.1", output);
        Assert.Contains("description: Test host", output);
    }

    // Add more tests for other function cases

    private static string GetConsoleOutput()
    {
        var builder = new StringBuilder();
        var writer = new StringWriter(builder);
        Console.SetOut(writer);

        return builder.ToString();
    }
}

