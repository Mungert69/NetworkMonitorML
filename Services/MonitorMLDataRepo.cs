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
using NetworkMonitor.ML.Services;

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
        var cachedResult = _cachedMonitorPingInfos.FirstOrDefault(mpi =>
                            mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID);
        int required = Math.Max(windowSize, cachedResult?.ModelConfig?.PredictWindow ?? 0);
        if (cachedResult != null && required > 0 && cachedResult.PingInfos.Count(IsUsable) >= required)
        {
            return cachedResult;
        }

        if (cachedResult != null)
        {
            _cachedMonitorPingInfos.Remove(cachedResult);
        }

        var refreshed = await GetDBMonitorPingInfo(monitorIPID, windowSize, dataSetID);
        if (refreshed != null)
        {
            _cachedMonitorPingInfos.RemoveAll(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID);
            _cachedMonitorPingInfos.Add(refreshed);
        }
        return refreshed;
    }


    public async Task<MonitorPingInfo?> GetDBWithContextMonitorPingInfo(int monitorIPID, int windowSize, int dataSetID, MonitorContext monitorContext)
    {
        // Query the latest or specific MonitorPingInfo
        var monitorIP = await monitorContext.MonitorIPs
            .AsNoTracking()
            .Include(ip => ip.ModelConfig)
            .FirstOrDefaultAsync(ip => ip.ID == monitorIPID);

        int resolvedWindowSize = windowSize;
        if (monitorIP?.ModelConfig?.PredictWindow is int hostWindow && hostWindow > resolvedWindowSize)
        {
            resolvedWindowSize = hostWindow;
        }

        var latestMonitorPingInfo = await monitorContext.MonitorPingInfos
            .AsNoTracking()
            .Include(mpi => mpi.PingInfos)
            .Include(mpi => mpi.PredictStatus)
            .Where(mpi => mpi.Enabled && mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID)
            .FirstOrDefaultAsync();

        if (latestMonitorPingInfo == null)
            return null;

        // Include timeout headroom when loading history, just as when updating
        // the cache. Query only older datasets, never the live dataset twice.
        int additionalPingInfosNeeded = Math.Max(0, PredictionWindow.Capacity(resolvedWindowSize) - latestMonitorPingInfo.PingInfos.Count);
        if (latestMonitorPingInfo.PingInfos.Count(IsUsable) < resolvedWindowSize && additionalPingInfosNeeded > 0)
        {
            var additionalPingInfos = await monitorContext.MonitorPingInfos
                .AsNoTracking()
                .Where(mpi => mpi.Enabled && mpi.MonitorIPID == monitorIPID && mpi.DataSetID > 0 &&
                    (dataSetID == 0 || mpi.DataSetID < dataSetID))
                .SelectMany(mpi => mpi.PingInfos)
                .OrderByDescending(pi => pi.DateSentInt)
                .Take(additionalPingInfosNeeded)
                .ToListAsync();

            latestMonitorPingInfo.PingInfos.AddRange(additionalPingInfos);
        }

        // Sort PingInfos by DateSentInt
        latestMonitorPingInfo.PingInfos.Sort((x, y) => x.DateSentInt.CompareTo(y.DateSentInt));

        if (monitorIP?.ModelConfig != null)
        {
            latestMonitorPingInfo.ModelConfig = monitorIP.ModelConfig;
        }

        latestMonitorPingInfo.PingInfos = PredictionWindow.Trim(latestMonitorPingInfo.PingInfos, resolvedWindowSize, IsUsable);

        return latestMonitorPingInfo;
    }

    public async Task<MonitorPingInfo?> GetDBMonitorPingInfo(int monitorIPID, int windowSize, int dataSetID)
    {
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

            var monitorIP = await monitorContext.MonitorIPs
                .AsNoTracking()
                .Include(ip => ip.ModelConfig)
                .FirstOrDefaultAsync(ip => ip.ID == monitorIPID);
            if (monitorIP?.ModelConfig != null)
            {
                latestMonitorPingInfo.ModelConfig = monitorIP.ModelConfig;
            }

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



    private void UpdateCachedPredictStatus(int monitorIPID, PredictStatus updated)
    {
        var cached = _cachedMonitorPingInfos
            .FirstOrDefault(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == 0);

        if (cached != null)
        {
            cached.PredictStatus = updated;
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
            updatedMonitorPingInfo.PingInfos.Sort((x, y) => x.DateSentInt.CompareTo(y.DateSentInt));
            updatedMonitorPingInfo.PingInfos = PredictionWindow.Trim(updatedMonitorPingInfo.PingInfos,
                Math.Max(_windowSize, updatedMonitorPingInfo.ModelConfig?.PredictWindow ?? 0), IsUsable);
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
                existingDateSents.Add(newPi.DateSentInt, newPi);
            }
        }

        // Sort once after all new elements are added.
        cachedMonitorPingInfo.PingInfos.Sort((x, y) => x.DateSentInt.CompareTo(y.DateSentInt));

        int limit = Math.Max(_windowSize, cachedMonitorPingInfo.ModelConfig?.PredictWindow ?? 0);
        cachedMonitorPingInfo.PingInfos = PredictionWindow.Trim(cachedMonitorPingInfo.PingInfos, limit, IsUsable);
    }

    private static bool IsUsable(PingInfo sample) => (sample.RoundTripTime ?? 0) < ushort.MaxValue;

    public async Task<ResultObj> UpdateMonitorPingInfoWithPredictionResultsById(int monitorIPID, int dataSetID, PredictStatus predictStatus)
    {
        var result = new ResultObj();

        using var scope = _scopeFactory.CreateScope();
        var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();

        try
        {
            // Step 1: Get MonitorPingInfo ID
            var monitorPingInfoID = await monitorContext.MonitorPingInfos
                .Where(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == dataSetID)
                .Select(mpi => mpi.ID)
                .FirstOrDefaultAsync();

            if (monitorPingInfoID == 0)
            {
                result.Success = false;
                result.Message = $"MonitorPingInfo not found for MonitorIPID {monitorIPID}, DataSetID {dataSetID}";
                _logger.LogError(result.Message);
                return result;
            }

            // Step 2: Get PredictStatus by FK (avoid Include)
            var dbPredictStatus = await monitorContext.PredictStatuses
                .FirstOrDefaultAsync(ps => ps.MonitorPingInfoID == monitorPingInfoID);

            if (dbPredictStatus == null)
            {
                // Create a fresh instance to guarantee a new auto-increment PK.
                var ps = new PredictStatus(predictStatus, zeroIds: true);
                ps.MonitorPingInfoID = monitorPingInfoID;
                monitorContext.PredictStatuses.Add(ps);
            }
            else
            {
                // Update existing (minimal fields only)
                dbPredictStatus.ChangeDetectionResult = predictStatus.ChangeDetectionResult;
                dbPredictStatus.SpikeDetectionResult = predictStatus.SpikeDetectionResult;
                dbPredictStatus.EventTime = predictStatus.EventTime;
                dbPredictStatus.Message = predictStatus.Message;
            }

            await monitorContext.SaveChangesAsync();
            UpdateCachedPredictStatus(monitorIPID, predictStatus);


            result.Success = true;
            result.Message = $"PredictStatus updated for MonitorIPID {monitorIPID}, DataSetID {dataSetID}";
            _logger.LogDebug(result.Message);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error updating PredictStatus: {ex.Message}";
            _logger.LogError(result.Message);
        }

        return result;
    }

    public async Task SearchOrCreatePredictStatus(MonitorPingInfo monitorPingInfo)
    {
        using var scope = _scopeFactory.CreateScope();
        var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();

        var recentStatus = await monitorContext.MonitorPingInfos
            .AsNoTracking()
            .Where(mpi => mpi.MonitorIPID == monitorPingInfo.MonitorIPID && mpi.PredictStatus != null)
            .OrderByDescending(mpi => mpi.ID)
            .Select(mpi => mpi.PredictStatus)
            .FirstOrDefaultAsync();

        monitorPingInfo.PredictStatus = recentStatus != null
            ? new PredictStatus(recentStatus) // Copy constructor clones values
            : new PredictStatus();
    }



    public async Task<ResultObj> UpdatePredictStatusFlags(int monitorIPID, bool? alertFlag, bool? sentFlag)
    {
        ResultObj result = new();

        using var scope = _scopeFactory.CreateScope();
        var monitorContext = scope.ServiceProvider.GetRequiredService<MonitorContext>();

        try
        {
            var monitorPingInfoID = await monitorContext.MonitorPingInfos
                .Where(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == 0)
                .Select(mpi => mpi.ID)
                .FirstOrDefaultAsync();

            if (monitorPingInfoID == 0)
            {
                result.Success = false;
                result.Message = $"DB MonitorPingInfo with MonitorIPID {monitorIPID} and DataSetID 0 not found.";
                _logger.LogError(result.Message);
                return result;
            }

            var predictStatus = await monitorContext.PredictStatuses
                .FirstOrDefaultAsync(p => p.MonitorPingInfoID == monitorPingInfoID);

            if (predictStatus == null)
            {
                result.Success = false;
                result.Message = $"PredictStatus not found for MonitorPingInfoID {monitorPingInfoID}.";
                _logger.LogError(result.Message);
                return result;
            }

            if (alertFlag != null) predictStatus.AlertFlag = alertFlag.Value;
            if (sentFlag != null) predictStatus.AlertSent = sentFlag.Value;

            // Also update in cache if available
            var cachedMonitorPingInfo = _cachedMonitorPingInfos
                .FirstOrDefault(mpi => mpi.MonitorIPID == monitorIPID && mpi.DataSetID == 0);
            if (cachedMonitorPingInfo?.PredictStatus != null)
            {
                if (alertFlag != null) cachedMonitorPingInfo.PredictStatus.AlertFlag = alertFlag.Value;
                if (sentFlag != null) cachedMonitorPingInfo.PredictStatus.AlertSent = sentFlag.Value;
            }

            await monitorContext.SaveChangesAsync();

            result.Success = true;
            result.Message = $"Success: Set Predict Flags for MonitorIPID {monitorIPID} and DataSetID 0.";
            _logger.LogDebug(result.Message);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error setting Predict Flags: {ex.Message}";
            _logger.LogError(result.Message);
        }

        return result;
    }

}

