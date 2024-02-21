using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Objects;
using System.Threading.Tasks;
using NetworkMonitor.Objects.ServiceMessage;
using NetworkMonitor.Data;
using NetworkMonitor.ML.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace NetworkMonitor.ML.Data;

public interface IMonitorMLDataRepo
{
    Task<MonitorPingInfo?> GetMonitorPingInfo(int monitorIPID, int windowSize);
    Task<List<LocalPingInfo>> GetLocalPingInfosForHost(int monitorPingInfoID);

}

public class MonitorMLDataRepo : IMonitorMLDataRepo
{
    private readonly IServiceScopeFactory _scopeFactory;

    public MonitorMLDataRepo(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<MonitorPingInfo?> GetMonitorPingInfo(int monitorIPID, int windowSize)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var latestMonitorPingInfo = await monitorContext.MonitorPingInfos
            .Include(mpi => mpi.PingInfos)
            .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == 0);
        if (latestMonitorPingInfo == null) return null;

        int additionalPingInfosNeeded = windowSize - latestMonitorPingInfo.PingInfos.Count;

        if (additionalPingInfosNeeded > 0)
        {
            var previousDataSetID = await monitorContext.MonitorPingInfos
                .Where(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID != 0)
                .MaxAsync(mpi => mpi.DataSetID);

            var previousMonitorPingInfo = await monitorContext.MonitorPingInfos
                .Include(mpi => mpi.PingInfos)
                .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == previousDataSetID);

            if (previousMonitorPingInfo != null)
            {
                var additionalPingInfos = previousMonitorPingInfo.PingInfos
                .OrderByDescending(pi => pi.DateSentInt)
                .Take(additionalPingInfosNeeded)
                .ToList();

                latestMonitorPingInfo.PingInfos.AddRange(additionalPingInfos);
            }
        }

        latestMonitorPingInfo.PingInfos = latestMonitorPingInfo.PingInfos
            .OrderBy(pi => pi.DateSentInt)
            .ToList();


            return latestMonitorPingInfo;
        }

    }

    public async Task<List<LocalPingInfo>> GetLocalPingInfosForHost(int monitorPingInfoID)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var _context = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var localPingInfos = await _context.PingInfos
       .Where(p => p.MonitorPingInfoID == monitorPingInfoID)
       .Select(p => new LocalPingInfo
       {
           DateSentInt = p.DateSentInt,
           RoundTripTime = p.RoundTripTime ?? 0,
           StatusID = p.StatusID
       }).ToListAsync();

            return localPingInfos;
        }

    }

}

