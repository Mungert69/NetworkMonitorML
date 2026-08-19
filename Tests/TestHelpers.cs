// Tests/TestHelpers.cs
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NetworkMonitor.Objects;
using NetworkMonitor.Objects.Factory;
using NetworkMonitor.Objects.Repository;   // IRabbitRepo, RabbitRepo
using NetworkMonitor.Utils.Helpers;        // SystemParamsHelper

using RabbitMQ.Client;                     // IConnection, IChannel, ConnectionFactory, ExchangeType
using NetworkMonitor.Connection;

namespace NetworkMonitorML.IntegrationTests
{
    public static class TestHelpers
    {
        private sealed class RabbitConnectionUnavailableException : Exception
        {
            public RabbitConnectionUnavailableException(string message)
                : base(message) { }
        }

        private static IConfiguration BuildConfig()
        {
            // Critical: load .env into process env for dotnet test before any reads
            EnvBootstrapper.EnsureLoaded();

            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();
        }

        private static ISystemParamsHelper BuildParamsHelper()
        {
            var cfg = BuildConfig();
            using var lf = LoggerFactory.Create(_ => { });
            ILogger<SystemParamsHelper> logger = lf.CreateLogger<SystemParamsHelper>();
            // SystemParamsHelper itself reads config and will see env already loaded
            return new SystemParamsHelper(cfg, logger);
        }

        public static SystemUrl LocalRabbitUrl()
            => BuildParamsHelper().GetSystemParams().ThisSystemUrl;

        public static IRabbitRepo? MakeRabbitRepo(SystemUrl sys)
        {
            const int startupRetryLimit = 3;
            var cfg = BuildConfig();
            var netConfig = new NetConnectConfig(cfg, AppContext.BaseDirectory)
            {
                MaxRetries = startupRetryLimit,
                RetryDelayMilliseconds = 500,
                IsRestrictedPublishPerm = false
            };

            // Ensure the repo uses the caller-provided connection details and secrets.
            netConfig.SetLocalSystemUrlAsync(sys).GetAwaiter().GetResult();

            var repo = new RabbitRepo(NullLogger<RabbitRepo>.Instance, netConfig);
            using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            ResultObj res;
            try
            {
                res = repo.ConnectAndSetUp(startupCts.Token, maxRetriesOverride: startupRetryLimit).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"[TestHelpers] RabbitMQ connection setup timed out for {sys.RabbitHostName}:{sys.RabbitPort}");
                return null;
            }
            if (!res.Success)
            {
                Console.Error.WriteLine($"[TestHelpers] RabbitMQ connection failed for {sys.RabbitHostName}:{sys.RabbitPort}: {res.Message}");
                return null;
            }
            return repo;
        }

        public static IConnection NewConn(SystemUrl sys)
        {
            var f = new ConnectionFactory
            {
                HostName = sys.RabbitHostName,
                Port = sys.RabbitPort,
                UserName = sys.RabbitUserName,
                Password = sys.RabbitPassword,
                VirtualHost = sys.RabbitVHost
            };

            var connectTask = f.CreateConnectionAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var completed = Task.WhenAny(connectTask, timeoutTask).GetAwaiter().GetResult();

            if (completed != connectTask)
            {
                Console.Error.WriteLine($"[TestHelpers] RabbitMQ connection timed out for {sys.RabbitHostName}:{sys.RabbitPort}");
                throw new RabbitConnectionUnavailableException($"RabbitMQ timeout at {sys.RabbitHostName}:{sys.RabbitPort}");
            }

            try
            {
                return connectTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TestHelpers] RabbitMQ connection error for {sys.RabbitHostName}:{sys.RabbitPort}: {ex.Message}");
                throw new RabbitConnectionUnavailableException($"RabbitMQ connection error at {sys.RabbitHostName}:{sys.RabbitPort}: {ex.Message}");
            }
        }

        public static FakeSpaceResponder? TryCreateResponder(SystemUrl sys, Func<JsonElement, string, IEnumerable<string>> handler)
        {
            try
            {
                return new FakeSpaceResponder(sys, handler);
            }
            catch (RabbitConnectionUnavailableException ex)
            {
                Console.Error.WriteLine($"[TestHelpers] {ex.Message}");
                return null;
            }
        }

        public sealed class FakeSpaceResponder : IAsyncDisposable
        {
            private readonly IConnection _conn;
            private readonly IChannel _ch;
            private readonly string _queue;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;
            private readonly Func<JsonElement, string, IEnumerable<string>> _handler;

            public FakeSpaceResponder(SystemUrl sys, Func<JsonElement, string, IEnumerable<string>> handler)
            {
                _conn = NewConn(sys);
                _ch = _conn.CreateChannelAsync().GetAwaiter().GetResult();

                _ch.ExchangeDeclareAsync("oa.chat.create", ExchangeType.Direct, durable: true).GetAwaiter().GetResult();
                _ch.ExchangeDeclareAsync("oa.chat.reply", ExchangeType.Direct, durable: true).GetAwaiter().GetResult();

                var qok = _ch.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true).GetAwaiter().GetResult();
                _queue = qok.QueueName;
                _ch.QueueBindAsync(_queue, "oa.chat.create", routingKey: "").GetAwaiter().GetResult();
                _ch.BasicQosAsync(0, 1, global: false).GetAwaiter().GetResult();

                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
                _loop = Task.Run(() => ConsumeLoopAsync(_cts.Token));
            }

            private async Task ConsumeLoopAsync(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await _ch.BasicGetAsync(_queue, autoAck: false);
                    if (msg == null)
                    {
                        await Task.Delay(25, ct);
                        continue;
                    }

                    try
                    {
                        using var json = JsonDocument.Parse(msg.Body.ToArray());
                        var root = json.RootElement;
                        var payload = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;

                        var replyKey = payload.TryGetProperty("reply_key", out var rk) ? rk.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(replyKey))
                        {
                            foreach (var chunk in _handler(payload, replyKey!))
                            {
                                var ce = new
                                {
                                    id = Guid.NewGuid().ToString("N"),
                                    type = "com.openai.chat.chunk",
                                    source = "fake-space",
                                    specversion = "1.0",
                                    time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                    data = new
                                    {
                                        id = $"chatcmpl-{Guid.NewGuid():N}",
                                        choices = new object[]
                                        {
                                            new { index = 0, delta = new { role = "assistant", content = chunk }, finish_reason = "stop" }
                                        }
                                    }
                                };
                                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ce));
                                await _ch.BasicPublishAsync("oa.chat.reply", replyKey!, body: body);
                            }

                            var end = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { data = new { @object = "stream.end" } }));
                            await _ch.BasicPublishAsync("oa.chat.reply", replyKey!, body: end);
                        }

                        await _ch.BasicAckAsync(msg.DeliveryTag, multiple: false);
                    }
                    catch
                    {
                        await _ch.BasicNackAsync(msg.DeliveryTag, multiple: false, requeue: false);
                    }
                }
            }

            public async ValueTask DisposeAsync()
            {
                try { _cts.Cancel(); } catch { }
                try { await _loop; } catch { }
                try { await _ch.CloseAsync(); } catch { }
                try { _ch.Dispose(); } catch { }
                try { await _conn.CloseAsync(); } catch { }
                try { _conn.Dispose(); } catch { }
            }
        }
    }

}
