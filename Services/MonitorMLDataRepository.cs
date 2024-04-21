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
    bool RemoveMonitorPingInfos(List<int> monitorIPIDs);
    ResultObj UpdateMonitorPingInfo(MonitorPingInfo updatedMonitorPingInfo);
    Task<ResultObj> UpdatePredictStatusFlags(int monitorIPID, bool? alertFlag, bool? sentFlag);
    Task SearchOrCreatePredictStatus(MonitorPingInfo monitorPingInfo);
}
public class MonitorMLDataRepo : IMonitorMLDataRepo
{
    private readonly IServiceScopeFactory _scopeFactory;
    private ILogger _logger;
    private int _windowSize;
    private bool _isDataFull = false;
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
            _isDataFull = true;
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
        // Query the latest or specific MonitorPingInfo
        var latestMonitorPingInfo = await monitorContext.MonitorPingInfos
            .AsNoTracking()
            .Include(mpi => mpi.PingInfos)
            .Include(mpi => mpi.PredictStatus)
            .Where(mpi => mpi.Enabled && mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID)
            .FirstOrDefaultAsync();

        if (latestMonitorPingInfo == null)
            return null;

        // Calculate if additional PingInfos are needed to reach the windowSize
        int additionalPingInfosNeeded = windowSize - latestMonitorPingInfo.PingInfos.Count;
        if (additionalPingInfosNeeded > 0)
        {
            // Determine the previous DataSetID
            int previousDataSetID = dataSetID == 0
                ? await monitorContext.MonitorPingInfos.AsNoTracking().MaxAsync(mpi => mpi.DataSetID)
                : dataSetID - 1;

            // Retrieve additional PingInfos from the previous dataset if available
            var additionalPingInfos = await monitorContext.MonitorPingInfos
                .AsNoTracking()
                .Where(mpi => mpi.Enabled && mpi.MonitorIPID == monitorIPID && mpi.DataSetID == previousDataSetID)
                .SelectMany(mpi => mpi.PingInfos)
                .OrderByDescending(pi => pi.DateSentInt)
                .Take(additionalPingInfosNeeded)
                .ToListAsync();

            latestMonitorPingInfo.PingInfos.AddRange(additionalPingInfos);
        }

        // Sort PingInfos by DateSentInt
        latestMonitorPingInfo.PingInfos.Sort((x, y) => x.DateSentInt.CompareTo(y.DateSentInt));

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
            .Include(p => p.PredictStatus)
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

    public bool RemoveMonitorPingInfos(List<int> monitorIPIDs)
    {
        if (!_isDataFull || monitorIPIDs == null || monitorIPIDs.Count == 0)
            return false;

        // Use a HashSet for O(1) complexity on lookups
        var idsToRemove = new HashSet<int>(monitorIPIDs);

        // Remove items directly without creating a temporary list
        _cachedMonitorPingInfos.RemoveAll(mpi => idsToRemove.Contains(mpi.MonitorIPID) && mpi.DataSetID == 0);

        return true;
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
        if (!_isDataFull)
        {
            result.Success = false;
            result.Message = " Error : Data is not yet full. Please wait.";
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
        cachedMonitorPingInfo.CopyForPredict(updatedMonitorPingInfo);

        // 3. Manage PingInfos
        ManagePingInfos(cachedMonitorPingInfo, updatedMonitorPingInfo.PingInfos);
        result.Success = true;
        result.Message = " Success : Updated MonitorPingInfo ";

        return result;
    }

    private void ManagePingInfos(MonitorPingInfo cachedMonitorPingInfo, List<PingInfo> updatedPingInfos)
    {
        if (updatedPingInfos == null || updatedPingInfos.Count == 0)
            return;

        // Use a dictionary for faster lookup and avoiding duplicates.
        var existingDateSents = new Dictionary<uint, PingInfo>(cachedMonitorPingInfo.PingInfos.Count);
        foreach (var pi in cachedMonitorPingInfo.PingInfos)
        {
            existingDateSents[pi.DateSentInt] = pi;
        }

        // Process each new ping info only if it doesn't exist already.
        foreach (var newPi in updatedPingInfos)
        {
            if (!existingDateSents.ContainsKey(newPi.DateSentInt))
            {
                cachedMonitorPingInfo.PingInfos.Add(newPi);
            }
        }

        // Sort once after all new elements are added.
        cachedMonitorPingInfo.PingInfos.Sort((x, y) => x.DateSentInt.CompareTo(y.DateSentInt));

        // Ensure the collection does not exceed the window size by removing the oldest entries first.
        int excess = cachedMonitorPingInfo.PingInfos.Count - _windowSize;
        if (excess > 0)
        {
            cachedMonitorPingInfo.PingInfos.RemoveRange(0, excess);
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
                    monitorPingInfo.PredictStatus = predictStatus;
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

    public async Task SearchOrCreatePredictStatus(MonitorPingInfo monitorPingInfo)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();
            int maxId = await monitorContext.MonitorPingInfos.Where(w => w.PredictStatus != null && w.MonitorIPID == monitorPingInfo.MonitorIPID).MaxAsync(m => m.ID);
            var maxMonitorPingInfo = await monitorContext.MonitorPingInfos.AsNoTracking().Where(w => w.ID == maxId).FirstOrDefaultAsync();
            if (maxMonitorPingInfo != null && maxMonitorPingInfo.PredictStatus != null)
            {
                maxMonitorPingInfo.PredictStatus.MonitorPingInfoID = 0;
                maxMonitorPingInfo.PredictStatus.ID = 0;
                monitorPingInfo.PredictStatus = maxMonitorPingInfo.PredictStatus;
            }
            else
            {
                monitorPingInfo.PredictStatus = new PredictStatus();
            }
        }
    }



    public async Task<ResultObj> UpdatePredictStatusFlags(int monitorIPID, bool? alertFlag, bool? sentFlag)
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
                    .FirstOrDefaultAsync(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == 0);
                var cachedMonitorPingInfo = _cachedMonitorPingInfos.FirstOrDefault(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == 0);

                if (monitorPingInfo == null)
                {
                    result.Success = false;
                    result.Message = $" Error : DB MonitorPingInfo with MonitorIPID {monitorIPID} and DataSetID 0 not found.";
                    _logger.LogError(result.Message);
                    return result;
                }
                if (cachedMonitorPingInfo == null)
                {
                    result.Success = false;
                    result.Message = $" Error : Cached MonitorPingInfo with MonitorIPID {monitorIPID} and DataSetID 0 not found.";
                    _logger.LogError(result.Message);
                    return result;
                }
                //var flag = false;
                // Update the MonitorPingInfo object with the prediction results
                if (monitorPingInfo.PredictStatus != null)
                {
                    if (alertFlag != null) monitorPingInfo.PredictStatus.AlertFlag = (bool)alertFlag;
                    if (sentFlag != null) monitorPingInfo.PredictStatus.AlertSent = (bool)sentFlag;
                }
                if (cachedMonitorPingInfo.PredictStatus != null)
                {
                    if (alertFlag != null) cachedMonitorPingInfo.PredictStatus.AlertFlag = (bool)alertFlag;
                    if (sentFlag != null) cachedMonitorPingInfo.PredictStatus.AlertSent = (bool)sentFlag;
                }
                await monitorContext.SaveChangesAsync();


                result.Success = true;
                result.Message = $" Success : Set Predict Flags for MonitorIPID {monitorIPID} and DataSetID 0.";
                _logger.LogDebug(result.Message);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Error : setting Predict Flags for MonitorIPID {monitorIPID} and DataSetID 0 : {ex.Message}";
                _logger.LogError(result.Message);
            }
        }

        return result;
    }

}








