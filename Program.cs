using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Data;
using NetworkMonitor.Objects.Factory;
using System;

namespace NetworkMonitor.ML
{
    public class Program
    {
        public static void Main(string[] args)
        {
            string appFile = "appsettings.json";
            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJsonFile(appFile, optional: false)
                .Build();

            IHost host = CreateHostBuilder(config).Build();

            using (IServiceScope scope = host.Services.CreateScope())
            {
                IServiceProvider services = scope.ServiceProvider;
                try
                {
                    MonitorContext context = services.GetRequiredService<MonitorContext>();
                    DbInitializer.Initialize(context);
                }
                catch (Exception ex)
                {
                    using var loggerFactory = LoggerFactory.Create(builder =>
                            {
                                builder
                                    .AddFilter("Microsoft", LogLevel.Warning)  // Log only warnings from Microsoft namespaces
                                    .AddFilter("System", LogLevel.Warning)     // Log only warnings from System namespaces
                                    .AddFilter("Program", LogLevel.Debug)      // Log all messages from Program class
                                    .AddConsole();                             // Add console logger
                            });

                    var logger = loggerFactory.CreateLogger<Program>();
                    logger.LogError("An error occurred while seeding the database. Error was : " + ex.ToString());
                }
            }

            host.Run();
        }

        public static IHostBuilder CreateHostBuilder(IConfigurationRoot config) =>
            Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration(builder =>
                {
                    builder.AddConfiguration(config);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    // Register your Startup class's ConfigureServices method
                    var startup = new Startup(hostContext.Configuration);
                    startup.ConfigureServices(services);
                });
    }
}
