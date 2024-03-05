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
    Task<MonitorPingInfo?> GetMonitorPingInfo(int monitorIPID, int dataSetID);
    //Task<List<LocalPingInfo>> GetLocalPingInfosForHost(int monitorPingInfoID);
    Task<ResultObj> UpdateMonitorPingInfoWithPredictionResultsById(int monitorIPID, int dataSetID, PredictStatus predictStatus);
    Task<List<(int monitorIPID, int dataSetID)>> GetMonitorIPIDDataSetIDs();
    Task<List<MonitorPingInfo>> GetLatestMonitorPingInfos(int windowSize);
    void RemoveMonitorPingInfos(List<int> monitorIPIDs);
    ResultObj UpdateMonitorPingInfo(MonitorPingInfo updatedMonitorPingInfo);
}

public class MonitorMLDataRepo : IMonitorMLDataRepo
{
    private readonly IServiceScopeFactory _scopeFactory;
    private ILogger _logger;
    private int _windowSize;
    private List<MonitorPingInfo> _cachedMonitorPingInfos = new List<MonitorPingInfo>();

    public MonitorMLDataRepo(ILogger<MonitorMLDataRepo> logger, IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<List<MonitorPingInfo>> GetLatestMonitorPingInfos(int windowSize)
    {
        _windowSize = windowSize;
        if (_cachedMonitorPingInfos == null || _cachedMonitorPingInfos.Count == 0)
        {
            _cachedMonitorPingInfos = await GetDBLatestMonitorPingInfos(windowSize);
        }

        return _cachedMonitorPingInfos
               .Where(mpi => mpi.DataSetID == 0)
               .ToList();
    }


    public async Task<List<MonitorPingInfo>> GetDBLatestMonitorPingInfos(int windowSize)
    {
        List<MonitorPingInfo> latestMonitorPingInfos = new List<MonitorPingInfo>();

        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();

            // First, get all MonitorIPIDs that have a DataSetID = 0 entry.
            var monitorIPIDs = await monitorContext.MonitorPingInfos.AsNoTracking()
                .Where(mpi => mpi.DataSetID == 0)
                .Select(mpi => mpi.MonitorIPID)
                .ToListAsync();

            // For each MonitorIPID, get the MonitorPingInfo with DataSetID = 0 and its PingInfos.
            foreach (var monitorIPID in monitorIPIDs)
            {
                var monitorPingInfo = await GetDBWithContextMonitorPingInfo(monitorIPID, windowSize, 0, monitorContext);
                if (monitorPingInfo != null)
                {
                    latestMonitorPingInfos.Add(monitorPingInfo);
                }
            }
        }

        return latestMonitorPingInfos;
    }

    public async Task<MonitorPingInfo?> GetMonitorPingInfo(int monitorIPID, int windowSize, int dataSetID)
    {
        // 1. Check the Cache
        var cachedResult = _cachedMonitorPingInfos.FirstOrDefault(mpi =>
                            mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID);
        if (cachedResult != null)
        {
            // Adjust if windowSize filtering is needed
            return cachedResult;
        }

        return await GetDBMonitorPingInfo(monitorIPID, windowSize, dataSetID);
    }


    public async Task<MonitorPingInfo?> GetDBWithContextMonitorPingInfo(int monitorIPID, int windowSize, int dataSetID, MonitorContext monitorContext)
    {
        var latestMonitorPingInfo = await monitorContext.MonitorPingInfos.AsNoTracking()
             .Where(w => w.Enabled && w.MonitorIPID == monitorIPID && w.DataSetID == dataSetID)
             .Include(mpi => mpi.PingInfos)
             .FirstOrDefaultAsync();

        if (latestMonitorPingInfo == null) return null;

        int additionalPingInfosNeeded = windowSize - latestMonitorPingInfo.PingInfos.Count;

        if (additionalPingInfosNeeded > 0)
        {
            int previousDataSetID;
            if (dataSetID == 0)
            {
                previousDataSetID = await monitorContext.MonitorPingInfos.AsNoTracking().MaxAsync(mpi => mpi.DataSetID);
            }
            else
            {
                previousDataSetID = dataSetID--;
            }

            var previousMonitorPingInfo = await monitorContext.MonitorPingInfos.AsNoTracking()
            .Where(w => w.Enabled && w.MonitorIPID == monitorIPID && w.DataSetID == previousDataSetID)
                .Include(mpi => mpi.PingInfos)
                .FirstOrDefaultAsync();

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
    public async Task<MonitorPingInfo?> GetDBMonitorPingInfo(int monitorIPID, int windowSize, int dataSetID)
    {
        _windowSize = windowSize;
        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();



            return await GetDBWithContextMonitorPingInfo(monitorIPID, windowSize, dataSetID, monitorContext);
        }

    }

    public async Task<MonitorPingInfo?> GetMonitorPingInfo(int monitorIPID, int dataSetID)
    {
        var cachedResult = _cachedMonitorPingInfos.FirstOrDefault(mpi =>
                            mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID);
        if (cachedResult != null)
        {
            // Adjust if windowSize filtering is needed
            return cachedResult;
        }

        return await GetDBMonitorPingInfo(monitorIPID, dataSetID);

    }
    public async Task<MonitorPingInfo?> GetDBMonitorPingInfo(int monitorIPID, int dataSetID)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            var latestMonitorPingInfo = await monitorContext.MonitorPingInfos.AsNoTracking()
            .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID);
            if (latestMonitorPingInfo == null) return null;

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
            var startOfYear2024 = new DateTime(2024, 1, 1);

            var monitorPingInfos = await monitorContext.MonitorPingInfos.AsNoTracking()
                .Where(mpi => mpi.DateEnded >= startOfYear2024 &&
                              monitorContext.PingInfos.Count(pi => pi.MonitorPingInfoID == mpi.ID) > 100)
                .Select(mpi => new { mpi.MonitorIPID, mpi.DataSetID })
                .ToListAsync();



            var result = monitorPingInfos
                .Select(mpi => (mpi.MonitorIPID, mpi.DataSetID))
                .ToList();

            return result;
        }
    }


    /*  public async Task<List<LocalPingInfo>> GetLocalPingInfosForHost(int monitorPingInfoID)
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

      }*/

    public void RemoveMonitorPingInfos(List<int> monitorIPIDs)
    {
        if (monitorIPIDs != null && monitorIPIDs.Count() != 0)
        {
            var removeMonitorPingInfos = _cachedMonitorPingInfos
                                         .Where(w => monitorIPIDs.Contains(w.MonitorIPID) && w.DataSetID == 0)
                                         .ToList();

            foreach (var itemToRemove in removeMonitorPingInfos)
            {
                _cachedMonitorPingInfos.Remove(itemToRemove);
            }
        }
    }



    public ResultObj UpdateMonitorPingInfo(MonitorPingInfo updatedMonitorPingInfo)
    {
        var result = new ResultObj();
        if (_cachedMonitorPingInfos == null)
        {
            result.Success = false;
            result.Message = " Error : Cache MonitorPingInfos is null";
            return result;
        }
        // 1. Find in cache
        var cachedMonitorPingInfo = _cachedMonitorPingInfos?.FirstOrDefault(mpi =>
                             mpi.MonitorIPID == updatedMonitorPingInfo.MonitorIPID && mpi.DataSetID == updatedMonitorPingInfo.DataSetID);

        if (cachedMonitorPingInfo == null)
        {
            _cachedMonitorPingInfos!.Add(updatedMonitorPingInfo);
            result.Success = true;
            result.Message = " Success : Added new MonitorPingInfo ";
            return result;
        }

        // 2. Update properties from the passed 'updatedMonitorPingInfo'
        cachedMonitorPingInfo.CopyMonitorPingInfo(updatedMonitorPingInfo, false);

        // 3. Manage PingInfos
        ManagePingInfos(cachedMonitorPingInfo, updatedMonitorPingInfo.PingInfos);
        result.Success = true;
        result.Message = " Success : Updated MonitorPingInfo ";

        return result;
    }

    // Helper method for managing PingInfos
    private void ManagePingInfos(MonitorPingInfo cachedMonitorPingInfo, List<PingInfo> updatedPingInfos)
    {

        // 1. Build a set of existing DateSentInt values:
        var existingDateSents = new HashSet<uint>(cachedMonitorPingInfo.PingInfos.Select(pi => pi.DateSentInt));

        // 2. Filter updatedPingInfos to remove duplicates:
        var newPingInfos = updatedPingInfos.Where(pi => !existingDateSents.Contains(pi.DateSentInt)).ToList();

        // 3. Remove oldest if necessary:
        while (cachedMonitorPingInfo.PingInfos.Count + newPingInfos.Count > _windowSize)
        {
            cachedMonitorPingInfo.PingInfos.RemoveAt(0); // Remove oldest
        }

        // 4. Add the filtered new PingInfos:
        cachedMonitorPingInfo.PingInfos.AddRange(newPingInfos);
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
                    .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID);

                if (monitorPingInfo == null)
                {
                    result.Success = false;
                    result.Message = $" Error : MonitorPingInfo with MonitorIPID {monitorIPID} and DataSetID {dataSetID} not found.";
                    _logger.LogError(result.Message);
                    return result;
                }
                //var flag = false;
                // Update the MonitorPingInfo object with the prediction results
                if (monitorPingInfo.PredictStatus == null)
                {
                    predictStatus.MonitorPingInfoID = monitorPingInfo.ID;
                    monitorContext.PredictStatuses.Add(predictStatus);

                }
                else
                {
                    monitorPingInfo.PredictStatus.ChangeDetectionResult = predictStatus.ChangeDetectionResult;
                    monitorPingInfo.PredictStatus.SpikeDetectionResult = predictStatus.SpikeDetectionResult;
                    monitorPingInfo.PredictStatus.EventTime = predictStatus.EventTime;
                    monitorPingInfo.PredictStatus.Message = predictStatus.Message;
                }
                await monitorContext.SaveChangesAsync();
                /*// Assuming PredictStatus can directly store the DetectionResult objects
                monitorPingInfo.PredictStatus.ChangeDetectionResult = predictStatus.ChangeDetectionResult;
                monitorPingInfo.PredictStatus.SpikeDetectionResult = predictStatus.SpikeDetectionResult;
                monitorPingInfo.PredictStatus.EventTime = predictStatus.EventTime;
                monitorPingInfo.PredictStatus.Message = predictStatus.Message;*/
                // Save changes to the database
                // Mark entity as modified if it's not tracking changes automatically
                //monitorContext.Entry(monitorPingInfo).State = EntityState.Modified;

                // Save changes to the database

                result.Success = true;
                result.Message = $" Success : MonitorPingInfo with MonitorIPID {monitorIPID} and DataSetID {dataSetID} updated with prediction results.";
                _logger.LogDebug(result.Message);
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






