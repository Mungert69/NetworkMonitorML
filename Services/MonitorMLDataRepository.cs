using System.Collections.Generic;
using System.Linq;
using System;
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
    Task<MonitorPingInfo?> GetMonitorPingInfo(int monitorIPID, int windowSize, int dataSetID);
    Task<List<LocalPingInfo>> GetLocalPingInfosForHost(int monitorPingInfoID);
    Task<ResultObj> UpdateMonitorPingInfoWithPredictionResultsById(int monitorIPID, int dataSetID, PredictStatus predictStatus);
    Task<List<(int monitorIPID, int dataSetID)>> GetMonitorIPIDDataSetIDs();
}

public class MonitorMLDataRepo : IMonitorMLDataRepo
{
    private readonly IServiceScopeFactory _scopeFactory;
    private ILogger _logger;

    public MonitorMLDataRepo(ILogger<MonitorMLDataRepo> logger, IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<MonitorPingInfo?> GetMonitorPingInfo(int monitorIPID, int windowSize, int dataSetID)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var latestMonitorPingInfo = await monitorContext.MonitorPingInfos
            .Include(mpi => mpi.PingInfos)
            .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID);
            if (latestMonitorPingInfo == null) return null;

            int additionalPingInfosNeeded = windowSize - latestMonitorPingInfo.PingInfos.Count;

            if (additionalPingInfosNeeded > 0)
            {
                int previousDataSetID;
                if (dataSetID == 0) previousDataSetID = await monitorContext.MonitorPingInfos
                    .Where(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID != 0)
                    .MaxAsync(mpi => mpi.DataSetID);
                else
                {
                    previousDataSetID = dataSetID--;
                }

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
    public async Task<List<(int monitorIPID, int dataSetID)>> GetMonitorIPIDDataSetIDs()
{
    using (var scope = _scopeFactory.CreateScope())
    {
        var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
        
        // Assuming you want to fetch MonitorPingInfos based on a certain condition
        // This example fetches all MonitorPingInfos, but you should adjust the Where clause as needed
        var monitorPingInfos = await monitorContext.MonitorPingInfos
            .Where(mpi => /* your condition here */ true) // Replace true with your actual condition
            .Select(mpi => new { mpi.MonitorIPID, mpi.DataSetID })
            .ToListAsync();

        var result = monitorPingInfos
            .Select(mpi => (mpi.MonitorIPID, mpi.DataSetID))
            .ToList();

        return result;
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

    public async Task<ResultObj> UpdateMonitorPingInfoWithPredictionResultsById(int monitorIPID, int dataSetID, PredictStatus predictStatus)
    {
        ResultObj result = new ResultObj();

        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();

            try
            {
                // Fetch the MonitorPingInfo object by ID
                var monitorPingInfo = await monitorContext.MonitorPingInfos
                    .Include(mpi => mpi.PredictStatus) // Include PredictStatus if it's a separate entity
                    .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID==dataSetID);

                if (monitorPingInfo == null)
                {
                    result.Success = false;
                    result.Message = $" Error : MonitorPingInfo with MonitorIPID {monitorIPID} and DataSetID {dataSetID} not found.";
                    _logger.LogError(result.Message);
                    return result;
                }

                // Update the MonitorPingInfo object with the prediction results
                if (monitorPingInfo.PredictStatus == null)
                {
                    monitorPingInfo.PredictStatus = new PredictStatus();
                }

                // Assuming PredictStatus can directly store the DetectionResult objects
                monitorPingInfo.PredictStatus.ChangeDetectionResult = predictStatus.ChangeDetectionResult;
                monitorPingInfo.PredictStatus.SpikeDetectionResult = predictStatus.SpikeDetectionResult;
                monitorPingInfo.PredictStatus.EventTime = predictStatus.EventTime;
                monitorPingInfo.PredictStatus.Message = predictStatus.Message;
                // Save changes to the database
                monitorContext.Update(monitorPingInfo);
                await monitorContext.SaveChangesAsync();

                result.Success = true;
                result.Message = $" Success : MonitorPingInfo with MonitorIPID {monitorIPID} and DataSetID {dataSetID} updated with prediction results.";
                _logger.LogInformation(result.Message);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Error : updating MonitorPingInfo with MonitorIPID {monitorIPID} and DataSetID {dataSetID} : {ex.Message}";
                _logger.LogError(result.Message);
            }
        }

        return result;
    }

}






