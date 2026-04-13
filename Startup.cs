using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetworkMonitor.ML.Services;
using NetworkMonitor.ML.Data;
using NetworkMonitor.Data;
using NetworkMonitor.Objects;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using NetworkMonitor.Utils;
using NetworkMonitor.Objects.Factory;
using NetworkMonitor.Objects.Repository;
using NetworkMonitor.ML.Model;
using HostInitActions;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Utils.Helpers;
using NetworkMonitor.Objects.ServiceMessage;
namespace NetworkMonitor.ML
{
    public class Startup
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

        public Startup(IConfiguration configuration)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            Configuration = configuration;
        }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

        public IConfiguration Configuration { get; }
        private IServiceCollection _services;
        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            _services = services;
            services.AddLogging(builder =>
                          {
                              builder.AddSimpleConsole(options =>
                        {
                            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                            options.IncludeScopes = true;
                        });

                          });
            string connectionString = Configuration.GetConnectionString("DefaultConnection") ?? "";
            services.AddDbContext<MonitorContext>(options =>
                options.UseMySql(connectionString,
                ServerVersion.AutoDetect(connectionString),
                mySqlOptions =>
                     {
                         mySqlOptions.EnableRetryOnFailure(
                         maxRetryCount: 5,
                         maxRetryDelay: TimeSpan.FromSeconds(10),
                         errorNumbersToAdd: null);
                         mySqlOptions.CommandTimeout(600);  // Set to 600 seconds, for example
                     }
            ));

            var modelSelection = Configuration.GetValue<string>("ModelSelection") ?? "TimesFM";

            services.AddSingleton<TimesFmModelFactory>(sp => new TimesFmModelFactory(
                sp.GetRequiredService<IRabbitRepo>(),
                sp.GetRequiredService<SystemParams>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<MLParams>()));

            services.AddSingleton<IMLModelFactory>(sp =>
            {
                if (string.Equals(modelSelection, "TimesFM", StringComparison.OrdinalIgnoreCase))
                {
                    return sp.GetRequiredService<TimesFmModelFactory>();
                }

                if (string.Equals(modelSelection, "MicrosoftMLTS", StringComparison.OrdinalIgnoreCase))
                {
                    return new MLModelFactory();
                }

                if (string.Equals(modelSelection, "Hybrid", StringComparison.OrdinalIgnoreCase))
                {
                    return new MLModelFactory();
                }

                throw new InvalidOperationException($"Unsupported ModelSelection value '{modelSelection}'. Valid options are 'TimesFM', 'MicrosoftMLTS', and 'Hybrid'.");
            });

            if (string.Equals(modelSelection, "Hybrid", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<ISecondaryModelFactory>(sp => sp.GetRequiredService<TimesFmModelFactory>());
            }

            services.AddSingleton<IMonitorMLDataRepo, MonitorMLDataRepo>();
            services.AddSingleton<IMonitorMLService, MonitorMLService>();
            services.AddSingleton<IRabbitListener, RabbitListener>();
            services.AddSingleton<IRabbitRepo, RabbitRepo>();
            services.AddSingleton<IFileRepo, FileRepo>(
                 provider =>
                 {
                     return new FileRepo(false, "./state");
                 }
             );
            services.AddSingleton<ISystemParamsHelper, SystemParamsHelper>();

            services.AddSingleton<MLParams>(sp =>
                       {
                           var systemParamsHelper = sp.GetRequiredService<ISystemParamsHelper>();
                           return systemParamsHelper.GetMLParams();
                       });
            services.AddSingleton<SystemParams>(sp =>
           {
               var systemParamsHelper = sp.GetRequiredService<ISystemParamsHelper>();
               return systemParamsHelper.GetSystemParams();
           });
            services.AddSingleton(_cancellationTokenSource);
            services.Configure<HostOptions>(s => s.ShutdownTimeout = TimeSpan.FromSeconds(30));
            services.AddAsyncServiceInitialization()
             .AddInitAction<IRabbitRepo>(async (rabbitRepo) =>
                    {
                        await rabbitRepo.ConnectAndSetUp(_cancellationTokenSource.Token);
                    })
                .AddInitAction<IMonitorMLService>(async (mlService) =>
                    {
                        await mlService.Init();
                    })
                 .AddInitAction<IRabbitListener>(async (rabbitListener) =>
                    {
                        await rabbitListener.Setup(_cancellationTokenSource.Token);
                    });
        }
        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IHostApplicationLifetime appLifetime)
        {

            appLifetime.ApplicationStopping.Register(() =>
            {
                _cancellationTokenSource.Cancel();

                var rabbitRepo = app.ApplicationServices.GetService<IRabbitRepo>();
                if (rabbitRepo != null)
                {
                    rabbitRepo.Shutdown().GetAwaiter().GetResult();
                }

                var rabbitListener = app.ApplicationServices.GetService<IRabbitListener>();
                if (rabbitListener != null)
                {
                    rabbitListener.Shutdown().GetAwaiter().GetResult();
                }
            });

        }
    }
}
