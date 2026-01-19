using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using DBSyncTool.Models;
using DBSyncTool.Helpers;

namespace DBSyncTool.Services
{
    public class CopyOrchestrator
    {
        private readonly AppConfiguration _config;
        private Tier2DataService _tier2Service;
        private readonly AxDbDataService _axDbService;
        private readonly Action<string> _logger;
        private readonly TimestampManager _timestampManager;

        private List<TableInfo> _tables = new List<TableInfo>();
        private CancellationTokenSource? _cancellationTokenSource;

        public event EventHandler<List<TableInfo>>? TablesUpdated;
        public event EventHandler<string>? StatusUpdated;
        public event EventHandler? TimestampsUpdated;

        public CopyOrchestrator(AppConfiguration config, Action<string> logger)
        {
            _config = config;
            _tier2Service = new Tier2DataService(config.Tier2Connection, logger);
            _axDbService = new AxDbDataService(config.AxDbConnection, logger);
            _logger = logger;
            _timestampManager = new TimestampManager();
            _timestampManager.LoadFromConfig(config);
        }

        public List<TableInfo> GetTables() => _tables.ToList();

        /// <summary>
        /// Stage 1: Discover Tables
        /// </summary>
        public async Task PrepareTableListAsync()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger("Starting Discover Tables...");
                _tables.Clear();

                // Parse and validate strategy overrides
                var strategyOverrides = ParseStrategyOverrides(_config.StrategyOverrides);

                // Get inclusion patterns early to check for single table optimization
                var inclusionPatterns = GetPatterns(_config.TablesToInclude);

                // Optimization: If only one specific table (no wildcard), pass it to discovery queries
                string? specificTableName = null;
                if (inclusionPatterns.Count == 1 && !inclusionPatterns[0].Contains("*"))
                {
                    // Convert to uppercase for SQLDICTIONARY queries (D365 stores table names in uppercase)
                    specificTableName = inclusionPatterns[0].ToUpper();
                    _logger($"Single table optimization: filtering discovery to '{specificTableName}'");
                }

                // ========== LOAD SQLDICTIONARY CACHES ONCE ==========
                _logger("─────────────────────────────────────────────");
                var tier2Cache = await _tier2Service.LoadSqlDictionaryCacheAsync(specificTableName);
                var axDbCache = await _axDbService.LoadSqlDictionaryCacheAsync(specificTableName);
                _logger("─────────────────────────────────────────────");
                // ====================================================

                // Discover tables from Tier2
                _logger("Discovering tables from Tier2...");
                var discoveredTables = await _tier2Service.DiscoverTablesAsync(specificTableName);
                _logger($"Discovered {discoveredTables.Count} tables");

                // Combine TablesToExclude and SystemExcludedTables
                var combinedExclusions = CombineExclusionPatterns(_config.TablesToExclude, _config.SystemExcludedTables);
                var exclusionPatterns = GetPatterns(combinedExclusions);

                var excludedFields = GetExcludedFieldsMap(_config.FieldsToExclude);

                int skipped = 0;
                int processed = 0;

                foreach (var (tableName, rowCount, sizeGB, bytesPerRow) in discoveredTables)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Apply inclusion patterns
                    if (!MatchesAnyPattern(tableName, inclusionPatterns))
                        continue;

                    // Apply exclusion patterns
                    if (MatchesAnyPattern(tableName, exclusionPatterns))
                    {
                        // If ShowExcludedTables is enabled and table has at least 1 record, add it with Excluded status
                        if (_config.ShowExcludedTables && rowCount > 0)
                        {
                            _tables.Add(new TableInfo
                            {
                                TableName = tableName,
                                Tier2RowCount = rowCount,
                                Tier2SizeGB = sizeGB,
                                Status = TableStatus.Excluded
                            });
                        }
                        skipped++;
                        continue;
                    }

                    // ========== USE CACHE INSTEAD OF DATABASE QUERIES ==========
                    // Get TableID from Tier2 cache (no database query!)
                    var tier2TableId = tier2Cache.GetTableId(tableName);
                    if (tier2TableId == null)
                    {
                        _logger($"Table {tableName} not found in Tier2 SQLDICTIONARY, skipping");
                        skipped++;
                        continue;
                    }

                    // Get TableID from AxDB cache (no database query!)
                    var axDbTableId = axDbCache.GetTableId(tableName);
                    if (axDbTableId == null)
                    {
                        _logger($"Table {tableName} not found in AxDB SQLDICTIONARY, skipping");
                        skipped++;
                        continue;
                    }

                    // Determine copy strategy
                    var strategy = GetStrategy(tableName, strategyOverrides);

                    // Get fields from caches (no database queries!)
                    var tier2Fields = tier2Cache.GetFields(tier2TableId.Value) ?? new List<string>();
                    var axDbFields = axDbCache.GetFields(axDbTableId.Value) ?? new List<string>();
                    // ===========================================================

                    // Calculate copyable fields (intersection minus excluded)
                    var copyableFields = tier2Fields.Intersect(axDbFields, StringComparer.OrdinalIgnoreCase).ToList();
                    var tableExcludedFields = excludedFields.ContainsKey(tableName.ToUpper())
                        ? excludedFields[tableName.ToUpper()]
                        : new List<string>();
                    var globalExcludedFields = excludedFields.ContainsKey("")
                        ? excludedFields[""]
                        : new List<string>();

                    copyableFields = copyableFields
                        .Where(f => !tableExcludedFields.Contains(f.ToUpper()))
                        .Where(f => !globalExcludedFields.Contains(f.ToUpper()))
                        .ToList();

                    if (copyableFields.Count == 0)
                    {
                        _logger($"Table {tableName} has no copyable fields, skipping");
                        skipped++;
                        continue;
                    }

                    // Generate fetch SQL
                    string fetchSql = GenerateFetchSql(tableName, copyableFields, strategy);

                    // Calculate records to copy based on strategy
                    long recordsToCopy = strategy.StrategyType switch
                    {
                        CopyStrategyType.RecId => strategy.RecIdCount ?? _config.DefaultRecordCount,
                        CopyStrategyType.Sql => strategy.RecIdCount ?? _config.DefaultRecordCount,
                        _ => _config.DefaultRecordCount  // Fallback
                    };

                    // Calculate estimated size in MB using minimum of RecordsToCopy and Tier2RowCount
                    long recordsForCalculation = Math.Min(recordsToCopy, rowCount);
                    decimal estimatedSizeMB = bytesPerRow > 0 && recordsForCalculation > 0
                        ? (decimal)bytesPerRow * recordsForCalculation / 1_000_000m
                        : 0;

                    // Check if optimized mode can be used (SysRowVersion optimization)
                    bool hasSysRowVersion = copyableFields
                        .Any(f => f.Equals("SYSROWVERSION", StringComparison.OrdinalIgnoreCase));

                    byte[]? tier2Ts = _timestampManager.GetTier2Timestamp(tableName);
                    byte[]? axdbTs = _timestampManager.GetAxDBTimestamp(tableName);

                    bool useOptimizedMode = hasSysRowVersion && tier2Ts != null && axdbTs != null;

                    // Create TableInfo
                    var tableInfo = new TableInfo
                    {
                        TableName = tableName,
                        TableId = tier2TableId.Value,
                        StrategyType = strategy.StrategyType,
                        RecIdCount = strategy.RecIdCount,
                        SqlTemplate = strategy.SqlTemplate,
                        UseTruncate = strategy.UseTruncate || _config.TruncateAllTables,
                        Tier2RowCount = rowCount,
                        Tier2SizeGB = sizeGB,
                        BytesPerRow = bytesPerRow,
                        RecordsToCopy = recordsToCopy,
                        EstimatedSizeMB = estimatedSizeMB,
                        FetchSql = fetchSql,
                        CopyableFields = copyableFields,
                        Status = TableStatus.Pending,
                        UseOptimizedMode = useOptimizedMode,
                        StoredTier2Timestamp = tier2Ts,
                        StoredAxDBTimestamp = axdbTs
                    };

                    _tables.Add(tableInfo);
                    processed++;
                }

                // Calculate total estimated size
                decimal totalEstimatedMB = _tables.Sum(t => t.EstimatedSizeMB);

                _logger($"Prepared {processed} tables, {skipped} skipped, {totalEstimatedMB:F2} MB to copy");
                OnStatusUpdated($"Prepared {processed} tables, {skipped} skipped, {totalEstimatedMB:F2} MB to copy");
                OnTablesUpdated();
            }
            catch (OperationCanceledException)
            {
                _logger("Discover Tables cancelled");
                OnStatusUpdated("Cancelled");
            }
            catch (Exception ex)
            {
                _logger($"ERROR: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stage 2: Process Tables (Fetch + Insert merged)
        /// Each worker fetches one table, inserts it, clears memory, then moves to next table
        /// </summary>
        public async Task ProcessTablesAsync()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger("─────────────────────────────────────────────");
                _logger("Starting Process Tables...");

                var pendingTables = _tables.Where(t => t.Status == TableStatus.Pending).ToList();
                if (pendingTables.Count == 0)
                {
                    _logger("No pending tables to process");
                    return;
                }

                var stopwatch = Stopwatch.StartNew();
                int completed = 0;
                int failed = 0;
                var semaphore = new SemaphoreSlim(_config.ParallelWorkers);

                var tasks = pendingTables.Select(async table =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await ProcessSingleTableAsync(table, cancellationToken);

                        if (table.Status == TableStatus.Inserted)
                            Interlocked.Increment(ref completed);
                        else
                            Interlocked.Increment(ref failed);

                        // Calculate progress and time estimates
                        var elapsed = stopwatch.Elapsed;
                        var processedCount = completed + failed;

                        // Calculate MB processed and remaining
                        decimal mbProcessed = _tables
                            .Where(t => t.Status == TableStatus.Inserted)
                            .Sum(t => t.EstimatedSizeMB);

                        decimal mbRemaining = _tables
                            .Where(t => t.Status == TableStatus.Pending || t.Status == TableStatus.Fetching || t.Status == TableStatus.Inserting)
                            .Sum(t => t.EstimatedSizeMB);

                        // Calculate estimated time left based on transfer rate
                        string estimatedTimeStr = "";
                        if (mbProcessed > 0 && elapsed.TotalSeconds > 0)
                        {
                            decimal mbPerSecond = mbProcessed / (decimal)elapsed.TotalSeconds;
                            if (mbPerSecond > 0)
                            {
                                decimal estimatedSecondsLeft = mbRemaining / mbPerSecond;
                                estimatedTimeStr = $" | Est: {FormatTime((int)estimatedSecondsLeft)}";
                            }
                        }

                        OnStatusUpdated($"Process Tables - {processedCount}/{pendingTables.Count} | Elapsed: {FormatTime((int)elapsed.TotalSeconds)}{estimatedTimeStr}");
                    }
                    finally
                    {
                        semaphore.Release();
                        OnTablesUpdated();
                    }
                });

                await Task.WhenAll(tasks);

                stopwatch.Stop();
                var totalTime = FormatTime((int)stopwatch.Elapsed.TotalSeconds);
                var totalRecordsToCopy = pendingTables.Sum(t => t.RecordsToCopy);
                var workers = _config.ParallelWorkers;
                var alias = _config.Alias;

                _logger($"Processed {completed} tables successfully, {failed} failed | Records: {totalRecordsToCopy:N0} | Workers: {workers} | Alias: {alias} | Total time: {totalTime}");
                OnStatusUpdated($"Processed {completed} tables, {failed} failed | Time: {totalTime}");
            }
            catch (OperationCanceledException)
            {
                _logger("Process Tables cancelled");
                OnStatusUpdated("Cancelled");
            }
            catch (Exception ex)
            {
                _logger($"ERROR: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stage 3: Retry Failed Tables
        /// Retries tables with FetchError or InsertError status
        /// </summary>
        public async Task RetryFailedAsync()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger("─────────────────────────────────────────────");
                _logger("Starting Retry Failed...");

                // Refresh Tier2 connection using the latest configuration before retrying
                _logger("Refreshing Tier2 connection before retry");
                _tier2Service = new Tier2DataService(_config.Tier2Connection, _logger);

                var failedTables = _tables
                    .Where(t => t.Status == TableStatus.FetchError ||
                               t.Status == TableStatus.InsertError)
                    .ToList();

                if (failedTables.Count == 0)
                {
                    _logger("No failed tables to retry");
                    return;
                }

                _logger($"Retrying {failedTables.Count} failed tables");

                // Reset failed tables to Pending for retry
                foreach (var table in failedTables)
                {
                    table.Status = TableStatus.Pending;
                    table.Error = string.Empty;
                    // Dispose and clear cached data to force re-fetch with fresh data
                    table.CachedData?.Dispose();
                    table.CachedData = null;
                    table.ControlData?.Dispose();
                    table.ControlData = null;
                }

                OnTablesUpdated();

                int completed = 0;
                int failed = 0;
                var semaphore = new SemaphoreSlim(_config.ParallelWorkers);

                var tasks = failedTables.Select(async table =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await ProcessSingleTableAsync(table, cancellationToken);

                        if (table.Status == TableStatus.Inserted)
                            Interlocked.Increment(ref completed);
                        else
                            Interlocked.Increment(ref failed);

                        OnStatusUpdated($"Retry Failed - {completed + failed}/{failedTables.Count} tables");
                    }
                    finally
                    {
                        semaphore.Release();
                        OnTablesUpdated();
                    }
                });

                await Task.WhenAll(tasks);

                _logger($"Retry completed: {completed} succeeded, {failed} failed");
                OnStatusUpdated($"Retry: {completed} succeeded, {failed} failed");
            }
            catch (OperationCanceledException)
            {
                _logger("Retry Failed cancelled");
                OnStatusUpdated("Cancelled");
            }
            catch (Exception ex)
            {
                _logger($"ERROR: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Re-apply copy strategy for a single table from current configuration
        /// </summary>
        private void ReapplyStrategyForTable(TableInfo table)
        {
            // Reload timestamps from config (they may have been updated)
            _logger($"[{table.TableName}] Reloading timestamps from config...");
            _timestampManager.LoadFromConfig(_config);

            // Parse strategy overrides from current config
            var strategyOverrides = ParseStrategyOverrides(_config.StrategyOverrides);

            // Get the strategy for this table
            var strategy = GetStrategy(table.TableName, strategyOverrides);

            // Regenerate fetch SQL with new strategy
            string fetchSql = GenerateFetchSql(table.TableName, table.CopyableFields, strategy);

            // Calculate records to copy based on strategy
            long recordsToCopy = strategy.StrategyType switch
            {
                CopyStrategyType.RecId => strategy.RecIdCount ?? _config.DefaultRecordCount,
                CopyStrategyType.Sql => strategy.RecIdCount ?? _config.DefaultRecordCount,
                _ => _config.DefaultRecordCount  // Fallback
            };

            // Calculate estimated size in MB using minimum of RecordsToCopy and Tier2RowCount
            long recordsForCalculation = Math.Min(recordsToCopy, table.Tier2RowCount);
            decimal estimatedSizeMB = table.BytesPerRow > 0 && recordsForCalculation > 0
                ? (decimal)table.BytesPerRow * recordsForCalculation / 1_000_000m
                : 0;

            // Check if optimized mode can be used (reload from config)
            bool hasSysRowVersion = table.CopyableFields
                .Any(f => f.Equals("SYSROWVERSION", StringComparison.OrdinalIgnoreCase));

            byte[]? tier2Ts = _timestampManager.GetTier2Timestamp(table.TableName);
            byte[]? axdbTs = _timestampManager.GetAxDBTimestamp(table.TableName);

            // Debug logging
            _logger($"[{table.TableName}] Has SysRowVersion: {hasSysRowVersion}, Tier2 TS: {(tier2Ts != null ? "found" : "null")}, AxDB TS: {(axdbTs != null ? "found" : "null")}");

            bool useOptimizedMode = hasSysRowVersion && tier2Ts != null && axdbTs != null;

            // Update table with new strategy
            table.StrategyType = strategy.StrategyType;
            table.RecIdCount = strategy.RecIdCount;
            table.SqlTemplate = strategy.SqlTemplate;
            table.UseTruncate = strategy.UseTruncate || _config.TruncateAllTables;
            table.RecordsToCopy = recordsToCopy;
            table.EstimatedSizeMB = estimatedSizeMB;
            table.FetchSql = fetchSql;
            table.UseOptimizedMode = useOptimizedMode;
            table.StoredTier2Timestamp = tier2Ts;
            table.StoredAxDBTimestamp = axdbTs;

            _logger($"Re-applied strategy for {table.TableName}: {table.StrategyDisplay}");
            if (useOptimizedMode)
            {
                _logger($"[{table.TableName}] Optimization enabled (timestamps found)");
            }
        }


        /// <summary>
        /// Process a single table by name (runs independently without semaphore)
        /// </summary>
        public async Task ProcessSingleTableByNameAsync(string tableName)
        {
            var table = _tables.FirstOrDefault(t =>
                t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));

            if (table == null)
            {
                _logger($"Table {tableName} not found");
                return;
            }

            // Skip if table is currently being processed
            if (table.Status == TableStatus.Fetching || table.Status == TableStatus.Inserting)
            {
                _logger($"Table {tableName} is currently being processed");
                return;
            }

            // Reset table state for re-processing
            table.Status = TableStatus.Pending;
            table.Error = string.Empty;
            table.CachedData?.Dispose();
            table.CachedData = null;
            table.ControlData?.Dispose();
            table.ControlData = null;
            table.RecordsFetched = 0;
            table.FetchTimeSeconds = 0;
            table.DeleteTimeSeconds = 0;
            table.InsertTimeSeconds = 0;
            table.MinRecId = 0;

            // Re-apply strategy from current configuration
            ReapplyStrategyForTable(table);

            // Check if strategy re-application resulted in an error
            if (table.Status == TableStatus.FetchError)
            {
                OnTablesUpdated();
                return;
            }

            OnTablesUpdated();

            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger($"─────────────────────────────────────────────");
                _logger($"Processing single table: {tableName}");

                await ProcessSingleTableAsync(table, cancellationToken);

                if (table.Status == TableStatus.Inserted)
                {
                    _logger($"Table {tableName} processed successfully");
                    OnStatusUpdated($"Completed: {tableName}");
                }
                else
                {
                    _logger($"Table {tableName} processing failed");
                    OnStatusUpdated($"Failed: {tableName}");
                }
            }
            catch (OperationCanceledException)
            {
                _logger($"Processing of {tableName} cancelled");
                OnStatusUpdated("Cancelled");
            }
            catch (Exception ex)
            {
                _logger($"ERROR processing {tableName}: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
            finally
            {
                OnTablesUpdated();
            }
        }

        /// <summary>
        /// Process table using SysRowVersion optimization (two-query approach)
        /// </summary>
        private async Task ProcessTableOptimizedAsync(TableInfo table, CancellationToken cancellationToken)
        {
            try
            {
                int recordCount = table.RecIdCount ?? _config.DefaultRecordCount;

                // Check if SQL strategy can use optimized mode (must have @sysRowVersionFilter placeholder)
                if (table.StrategyType == CopyStrategyType.Sql)
                {
                    if (string.IsNullOrWhiteSpace(table.SqlTemplate) ||
                        !table.SqlTemplate.Contains("@sysRowVersionFilter", StringComparison.OrdinalIgnoreCase))
                    {
                        // SQL strategy without placeholder: fall back to standard mode
                        table.UseOptimizedMode = false;
                        await ProcessTableStandardModeAsync(table, cancellationToken);
                        return;
                    }
                }

                // STEP 1: Fetch control data (RecId, SysRowVersion)
                _logger($"[{table.TableName}] Optimized mode: Fetching control data");
                table.Status = TableStatus.Fetching;
                OnTablesUpdated();

                // Pass SQL template for SQL strategy (will replace * with RecId, SysRowVersion)
                string? sqlTemplate = table.StrategyType == CopyStrategyType.Sql
                    ? table.SqlTemplate
                    : null;

                var controlStopwatch = Stopwatch.StartNew();
                DataTable controlData = await _tier2Service.FetchControlDataAsync(
                    table.TableName,
                    recordCount,
                    sqlTemplate,
                    cancellationToken);
                controlStopwatch.Stop();

                table.ControlData = controlData;
                _logger($"[{table.TableName}] Control query: {controlData.Rows.Count} records in {controlStopwatch.Elapsed.TotalSeconds:F2}s");

                if (controlData.Rows.Count == 0)
                {
                    _logger($"[{table.TableName}] No data in Tier2, skipping");
                    controlData.Dispose();
                    table.ControlData = null;
                    table.Status = TableStatus.Inserted;
                    return;
                }

                // Calculate min RecId and max SysRowVersion from control data
                long minRecId = controlData.AsEnumerable().Min(r => r.Field<long>("RecId"));
                byte[] tier2MaxTimestamp = controlData.AsEnumerable()
                    .Select(r => r.Field<byte[]>("SysRowVersion"))
                    .Max(new TimestampComparer())!;

                // STEP 2: Evaluate change volume
                long tier2ChangedCount = controlData.AsEnumerable()
                    .Count(r => TimestampHelper.CompareTimestamp(r.Field<byte[]>("SysRowVersion"), table.StoredTier2Timestamp) > 0);

                long axdbChangedCount = await _axDbService.GetChangedCountAsync(
                    table.TableName,
                    table.StoredAxDBTimestamp!,
                    cancellationToken);

                long axdbTotalCount = await _axDbService.GetRowCountAsync(table.TableName, cancellationToken);

                long totalChanges = tier2ChangedCount + axdbChangedCount;
                double changePercent = controlData.Rows.Count > 0
                    ? (double)totalChanges / controlData.Rows.Count * 100
                    : 0;

                double excessPercent = controlData.Rows.Count > 0
                    ? (double)(axdbTotalCount - controlData.Rows.Count) / controlData.Rows.Count * 100
                    : 0;

                bool useTruncate = changePercent > _config.TruncateThresholdPercent ||
                                   excessPercent > _config.TruncateThresholdPercent;

                table.Tier2ChangedCount = tier2ChangedCount;
                table.AxDBChangedCount = axdbChangedCount;
                table.ChangePercent = changePercent;
                table.ExcessPercent = excessPercent;
                table.UsedTruncate = useTruncate;

                _logger($"[{table.TableName}] Tier2 changes: {tier2ChangedCount}, AxDB changes: {axdbChangedCount}, " +
                       $"Total: {changePercent:F1}%, Excess: {excessPercent:F1}%");

                if (useTruncate)
                {
                    _logger($"[{table.TableName}] Using TRUNCATE mode (threshold: {_config.TruncateThresholdPercent}%)");
                    await ProcessTableTruncateModeAsync(table, tier2MaxTimestamp, cancellationToken);
                }
                else
                {
                    _logger($"[{table.TableName}] Using INCREMENTAL mode");
                    await ProcessTableIncrementalModeAsync(table, tier2MaxTimestamp, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose control data on cancellation - don't keep for retry in optimized mode
                table.ControlData?.Dispose();
                table.ControlData = null;
                table.CachedData?.Dispose();
                table.CachedData = null;
                table.Status = TableStatus.FetchError;
                table.Error = "Cancelled";
                _logger($"Cancelled {table.TableName}");
                throw;
            }
            catch (Exception ex)
            {
                // Dispose control data on error - don't keep for retry in optimized mode
                table.ControlData?.Dispose();
                table.ControlData = null;
                table.CachedData?.Dispose();
                table.CachedData = null;
                table.Status = table.Status == TableStatus.Fetching ? TableStatus.FetchError : TableStatus.InsertError;
                table.Error = ex.Message;
                _logger($"ERROR processing {table.TableName}: {ex.Message}");
            }
        }

        /// <summary>
        /// TRUNCATE mode: Full table refresh
        /// </summary>
        private async Task ProcessTableTruncateModeAsync(TableInfo table, byte[] tier2MaxTimestamp, CancellationToken cancellationToken)
        {
            // Fetch full data
            var fetchStopwatch = Stopwatch.StartNew();
            DataTable data = await _tier2Service.FetchDataBySqlAsync(
                table.TableName,
                table.FetchSql,
                cancellationToken);

            table.CachedData = data;
            table.RecordsFetched = data.Rows.Count;
            table.FetchTimeSeconds = (decimal)fetchStopwatch.Elapsed.TotalSeconds;

            _logger($"[{table.TableName}] Fetched {data.Rows.Count} records in {table.FetchTimeSeconds:F2}s");

            // Insert with truncate
            table.Status = TableStatus.Inserting;
            table.UseTruncate = true;  // Force TRUNCATE instead of delta comparison
            table.TruncateThresholdPercent = _config.TruncateThresholdPercent;  // Pass threshold for delta comparison optimization
            OnTablesUpdated();

            await _axDbService.InsertDataAsync(table, cancellationToken);

            // Update timestamps on success
            using var connection = new SqlConnection(_axDbService.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            byte[]? axdbMaxTimestamp = await _axDbService.GetMaxTimestampAsync(table.TableName, connection, null);

            if (axdbMaxTimestamp != null)
            {
                _timestampManager.SetTimestamps(table.TableName, tier2MaxTimestamp, axdbMaxTimestamp);
                _timestampManager.SaveToConfig(_config);
                OnTimestampsUpdated(); // Trigger auto-save to disk
            }

            table.CachedData?.Dispose();
            table.CachedData = null;
            table.ControlData?.Dispose();
            table.ControlData = null;  // Free memory after successful operation
            table.Status = TableStatus.Inserted;

            _logger($"[{table.TableName}] Completed (TRUNCATE mode)");
        }

        /// <summary>
        /// INCREMENTAL mode: Delete changed/deleted, insert changed/new
        /// </summary>
        private async Task ProcessTableIncrementalModeAsync(TableInfo table, byte[] tier2MaxTimestamp, CancellationToken cancellationToken)
        {
            table.Status = TableStatus.Inserting;
            OnTablesUpdated();

            // OPTIMIZATION: When no changes and no excess records, skip DELETE operations
            // Only check for missing records to insert
            bool hasChanges = table.Tier2ChangedCount > 0 || table.AxDBChangedCount > 0;
            bool hasExcess = table.ExcessPercent > 0;

            if (!hasChanges && !hasExcess)
            {
                _logger($"[{table.TableName}] No changes detected, checking for missing records only");

                // Quick check: Get existing RecIds from AxDB (no transaction needed for read)
                using var readConnection = new SqlConnection(_axDbService.GetConnectionString());
                await readConnection.OpenAsync(cancellationToken);

                var existingRecIds = await _axDbService.GetRecIdSetAsync(
                    table.TableName,
                    readConnection,
                    null,  // No transaction for read-only
                    cancellationToken);

                var tier2RecIds = table.ControlData!.AsEnumerable()
                    .Select(r => r.Field<long>("RecId"))
                    .ToHashSet();

                var missingRecIds = tier2RecIds.Where(id => !existingRecIds.Contains(id)).ToHashSet();

                if (missingRecIds.Count == 0)
                {
                    // Perfect sync - nothing to do!
                    _logger($"[{table.TableName}] Already in perfect sync, skipping all operations");
                    table.InsertTimeSeconds = 0;
                    table.DeleteTimeSeconds = 0;
                    table.ControlData?.Dispose();
                    table.ControlData = null;  // Free memory
                    table.Status = TableStatus.Inserted;

                    // Still update timestamps to current max
                    byte[]? axdbMaxTimestamp = await _axDbService.GetMaxTimestampAsync(table.TableName, readConnection, null);
                    _timestampManager.SetTimestamps(table.TableName, tier2MaxTimestamp, axdbMaxTimestamp ?? tier2MaxTimestamp);
                    _timestampManager.SaveToConfig(_config);
                    OnTimestampsUpdated();

                    _logger($"[{table.TableName}] Completed (INCREMENTAL mode - no changes)");
                    return;
                }

                // Missing records exist - insert them without DELETE operations
                _logger($"[{table.TableName}] Inserting {missingRecIds.Count} missing records (skipping DELETE operations)");
                // Fall through to normal insert logic below (but skip DELETEs)
            }

            using var connection = new SqlConnection(_axDbService.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            try
            {
                // Disable triggers
                string disableTriggers = $"ALTER TABLE [{table.TableName}] DISABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {disableTriggers}");
                await ExecuteNonQueryAsync(disableTriggers, connection, transaction);

                // Execute incremental deletes (skip if no changes and no excess)
                if (hasChanges || hasExcess)
                {
                    await _axDbService.ExecuteIncrementalDeletesAsync(
                        table,
                        table.ControlData!,
                        table.StoredTier2Timestamp!,
                        table.StoredAxDBTimestamp!,
                        connection,
                        transaction,
                        cancellationToken);
                }
                else
                {
                    _logger($"[{table.TableName}] Skipping DELETE operations (no changes, no excess)");
                    table.DeleteTimeSeconds = 0;
                }

                // Get remaining RecIds in AxDB
                var existingRecIds = await _axDbService.GetRecIdSetAsync(
                    table.TableName,
                    connection,
                    transaction,
                    cancellationToken);

                // Find RecIds to insert (in Tier2 but not in AxDB)
                var tier2RecIds = table.ControlData!.AsEnumerable()
                    .Select(r => r.Field<long>("RecId"))
                    .ToHashSet();

                var missingRecIds = tier2RecIds.Where(id => !existingRecIds.Contains(id)).ToHashSet();

                if (missingRecIds.Count == 0)
                {
                    _logger($"[{table.TableName}] No records to insert");
                    table.InsertTimeSeconds = 0;
                }
                else
                {
                    // Step 2.1: Calculate threshold for fetching
                    // Get timestamps for missing RecIds from control data
                    var missingTimestamps = table.ControlData!.AsEnumerable()
                        .Where(r => missingRecIds.Contains(r.Field<long>("RecId")))
                        .Select(r => r.Field<byte[]?>("SysRowVersion"))
                        .Where(t => t != null)  // Filter out null timestamps
                        .ToList();

                    // If no valid timestamps found, use minimum timestamp (all zeros)
                    byte[] minMissingTimestamp = missingTimestamps.Any()
                        ? missingTimestamps.Min(new TimestampComparer())!
                        : new byte[8];

                    byte[] fetchThreshold = TimestampHelper.MinTimestamp(minMissingTimestamp, table.StoredTier2Timestamp) ?? new byte[8];
                    long minRecId = table.ControlData!.AsEnumerable().Min(r => r.Field<long>("RecId"));

                    // Step 2.2: Fetch data from Tier2 using timestamp threshold (includes more records than needed)
                    _logger($"[{table.TableName}] Fetching missing records (expected {missingRecIds.Count})");
                    var fetchStopwatch = Stopwatch.StartNew();

                    // Pass SQL template for SQL strategy (will use @sysRowVersionFilter placeholder if present)
                    string? sqlTemplate = table.StrategyType == CopyStrategyType.Sql
                        ? table.SqlTemplate
                        : null;

                    DataTable tier2Data = await _tier2Service.FetchDataByTimestampAsync(
                        table.TableName,
                        table.CopyableFields,
                        table.RecIdCount ?? _config.DefaultRecordCount,
                        fetchThreshold,
                        minRecId,
                        sqlTemplate,
                        cancellationToken);

                    table.FetchTimeSeconds = (decimal)fetchStopwatch.Elapsed.TotalSeconds;
                    _logger($"[{table.TableName}] Fetched {tier2Data.Rows.Count} records from Tier2");

                    // Step 2.3: Filter out records that already exist in AxDB
                    var rowsToInsert = tier2Data.AsEnumerable()
                        .Where(r => !existingRecIds.Contains(r.Field<long>("RecId")))
                        .ToList();

                    _logger($"[{table.TableName}] After filtering: {rowsToInsert.Count} records to insert");

                    if (rowsToInsert.Count > 0)
                    {
                        using (DataTable filteredData = tier2Data.Clone())
                        {
                            foreach (var row in rowsToInsert)
                            {
                                filteredData.ImportRow(row);
                            }

                            // Step 2.4: Bulk insert
                            var insertStopwatch = Stopwatch.StartNew();
                            using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                            {
                                bulkCopy.DestinationTableName = table.TableName;
                                bulkCopy.BatchSize = 10000;
                                bulkCopy.BulkCopyTimeout = _config.AxDbConnection.CommandTimeout;

                                foreach (DataColumn col in filteredData.Columns)
                                {
                                    bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                                }

                                await bulkCopy.WriteToServerAsync(filteredData, cancellationToken);
                            }

                            table.InsertTimeSeconds = (decimal)insertStopwatch.Elapsed.TotalSeconds;
                            table.RecordsFetched = filteredData.Rows.Count;
                            _logger($"[{table.TableName}] Inserted {filteredData.Rows.Count} records");
                        }
                    }
                    else
                    {
                        table.InsertTimeSeconds = 0;
                    }

                    tier2Data.Dispose();  // Free memory after use
                }

                // Enable triggers
                string enableTriggers = $"ALTER TABLE [{table.TableName}] ENABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {enableTriggers}");
                await ExecuteNonQueryAsync(enableTriggers, connection, transaction);

                // Update sequence
                await UpdateSequenceAsync(table, connection, transaction, cancellationToken);

                // Commit transaction
                transaction.Commit();

                // Update timestamps
                byte[]? axdbMaxTimestamp = await _axDbService.GetMaxTimestampAsync(table.TableName, connection, null);
                if (axdbMaxTimestamp != null)
                {
                    _timestampManager.SetTimestamps(table.TableName, tier2MaxTimestamp, axdbMaxTimestamp);
                    _timestampManager.SaveToConfig(_config);
                    OnTimestampsUpdated(); // Trigger auto-save to disk
                }

                table.ControlData?.Dispose();
                table.ControlData = null;  // Free memory after successful operation
                table.Status = TableStatus.Inserted;
                _logger($"[{table.TableName}] Completed (INCREMENTAL mode)");
            }
            catch
            {
                // Dispose control data on error
                table.ControlData?.Dispose();
                table.ControlData = null;

                try
                {
                    transaction.Rollback();
                    await ExecuteNonQueryAsync($"ALTER TABLE [{table.TableName}] ENABLE TRIGGER ALL", connection, null);
                }
                catch { }
                throw;
            }
        }

        private async Task ExecuteNonQueryAsync(string sql, SqlConnection connection, SqlTransaction? transaction)
        {
            using var command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync();
        }

        private async Task UpdateSequenceAsync(TableInfo table, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
        {
            string maxRecIdSql = $"SELECT MAX(RecId) FROM [{table.TableName}]";
            using var cmd1 = new SqlCommand(maxRecIdSql, connection, transaction);
            var maxRecIdResult = await cmd1.ExecuteScalarAsync(cancellationToken);

            if (maxRecIdResult == null || maxRecIdResult == DBNull.Value)
                return;

            long maxRecId = Convert.ToInt64(maxRecIdResult);
            string sequenceName = $"SEQ_{table.TableId}";

            string currentSeqSql = "SELECT CAST(current_value AS BIGINT) FROM sys.sequences WHERE name = @SequenceName";
            using var cmd2 = new SqlCommand(currentSeqSql, connection, transaction);
            cmd2.Parameters.AddWithValue("@SequenceName", sequenceName);
            var currentSeqResult = await cmd2.ExecuteScalarAsync(cancellationToken);

            if (currentSeqResult == null || currentSeqResult == DBNull.Value)
                return;

            long currentSeq = Convert.ToInt64(currentSeqResult);
            long newSeq = Math.Max(maxRecId, currentSeq) + 10;

            string updateSeqSql = $"ALTER SEQUENCE [{sequenceName}] RESTART WITH {newSeq}";
            _logger($"[AxDB SQL] {updateSeqSql}");

            using var cmd3 = new SqlCommand(updateSeqSql, connection, transaction);
            await cmd3.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>
        /// Process table using standard mode (fetch → compare → delete → insert)
        /// </summary>
        private async Task ProcessTableStandardModeAsync(TableInfo table, CancellationToken cancellationToken)
        {
            try
            {
                // STAGE 1: FETCH
                table.Status = TableStatus.Fetching;
                table.Error = string.Empty;
                OnTablesUpdated();

                var fetchStopwatch = Stopwatch.StartNew();
                DataTable data = await _tier2Service.FetchDataBySqlAsync(
                    table.TableName,
                    table.FetchSql,
                    cancellationToken);

                table.CachedData = data;
                table.RecordsFetched = data.Rows.Count;
                table.MinRecId = GetMinRecIdFromData(data);
                table.FetchTimeSeconds = (decimal)fetchStopwatch.Elapsed.TotalSeconds;

                // Update records to copy and estimated size with actual fetched count
                table.RecordsToCopy = data.Rows.Count;
                long recordsForCalculation = Math.Min(data.Rows.Count, table.Tier2RowCount);
                table.EstimatedSizeMB = table.BytesPerRow > 0 && recordsForCalculation > 0
                    ? (decimal)table.BytesPerRow * recordsForCalculation / 1_000_000m
                    : 0;

                _logger($"Fetched {table.TableName}: {table.RecordsFetched} records in {table.FetchTimeSeconds:F2}s");

                // Check for cancellation before insert
                cancellationToken.ThrowIfCancellationRequested();

                // Smart TRUNCATE detection: If table has SysRowVersion and AxDB has excess records, use TRUNCATE
                if (!table.UseTruncate && table.CopyableFields.Any(f => f.Equals("SYSROWVERSION", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        long axdbTotalCount = await _axDbService.GetRowCountAsync(table.TableName, cancellationToken);
                        double excessPercent = table.RecordsFetched > 0
                            ? (double)(axdbTotalCount - table.RecordsFetched) / table.RecordsFetched * 100
                            : 0;

                        if (excessPercent > _config.TruncateThresholdPercent)
                        {
                            _logger($"[{table.TableName}] Detected excess records in AxDB: {axdbTotalCount:N0} total vs {table.RecordsFetched:N0} syncing ({excessPercent:F1}%)");
                            _logger($"[{table.TableName}] Auto-enabling TRUNCATE mode (threshold: {_config.TruncateThresholdPercent}%)");
                            table.UseTruncate = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger($"[{table.TableName}] Warning: Could not check AxDB row count: {ex.Message}");
                    }
                }

                // STAGE 2: INSERT (includes delete and insert operations)
                table.Status = TableStatus.Inserting;
                table.TruncateThresholdPercent = _config.TruncateThresholdPercent;  // Pass threshold for delta comparison optimization
                OnTablesUpdated();

                await _axDbService.InsertDataAsync(table, cancellationToken);

                _logger($"Deleted {table.TableName}: {table.DeleteTimeSeconds:F2}s, Inserted: {table.InsertTimeSeconds:F2}s");

                // Update timestamps if table has SysRowVersion (for future optimization)
                if (table.CopyableFields.Any(f => f.Equals("SYSROWVERSION", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var connection = new SqlConnection(_axDbService.GetConnectionString());
                        await connection.OpenAsync(cancellationToken);

                        byte[]? tier2MaxTs = table.CachedData?.AsEnumerable()
                            .Select(r => r.Field<byte[]>("SYSROWVERSION"))
                            .Where(ts => ts != null)
                            .Max(new TimestampComparer());

                        byte[]? axdbMaxTs = await _axDbService.GetMaxTimestampAsync(table.TableName, connection, null);

                        if (tier2MaxTs != null && axdbMaxTs != null)
                        {
                            _timestampManager.SetTimestamps(table.TableName, tier2MaxTs, axdbMaxTs);
                            _timestampManager.SaveToConfig(_config);
                            OnTimestampsUpdated(); // Trigger auto-save to disk
                            _logger($"[{table.TableName}] Timestamps saved for future optimization");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger($"[{table.TableName}] Warning: Could not save timestamps: {ex.Message}");
                    }
                }

                // STAGE 3: CLEANUP MEMORY (on success only)
                table.CachedData?.Dispose();
                table.CachedData = null;  // Clear memory immediately
                table.Status = TableStatus.Inserted;
                table.Error = string.Empty;

                var totalTime = table.FetchTimeSeconds + table.CompareTimeSeconds + table.DeleteTimeSeconds + table.InsertTimeSeconds;

                if (table.ComparisonUsed)
                {
                    _logger($"Completed {table.TableName}: Total time {totalTime:F2}s (Fetch: {table.FetchTimeSeconds:F2}s, Compare: {table.CompareTimeSeconds:F2}s, Delete: {table.DeleteTimeSeconds:F2}s, Insert: {table.InsertTimeSeconds:F2}s)");
                }
                else
                {
                    _logger($"Completed {table.TableName}: Total time {totalTime:F2}s (Fetch: {table.FetchTimeSeconds:F2}s, Delete: {table.DeleteTimeSeconds:F2}s, Insert: {table.InsertTimeSeconds:F2}s)");
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose CachedData - retry will re-fetch anyway
                table.CachedData?.Dispose();
                table.CachedData = null;
                table.Status = TableStatus.FetchError;
                table.Error = "Cancelled";
                _logger($"Cancelled {table.TableName}");
                throw;
            }
            catch (Exception ex)
            {
                // Dispose CachedData on error - retry will re-fetch anyway
                table.CachedData?.Dispose();
                table.CachedData = null;

                // Determine which stage failed based on current status
                if (table.Status == TableStatus.Fetching)
                {
                    table.Status = TableStatus.FetchError;
                }
                else if (table.Status == TableStatus.Inserting)
                {
                    table.Status = TableStatus.InsertError;
                }

                table.Error = ex.Message;
                _logger($"ERROR processing {table.TableName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Process a single table: fetch → insert → clear memory
        /// </summary>
        private async Task ProcessSingleTableAsync(TableInfo table, CancellationToken cancellationToken)
        {
            // Force truncate mode skips optimization - just truncate and insert
            if (table.UseTruncate)
            {
                await ProcessTableStandardModeAsync(table, cancellationToken);
                return;
            }

            // Route to optimized mode if available
            if (table.UseOptimizedMode)
            {
                await ProcessTableOptimizedAsync(table, cancellationToken);
                return;
            }

            // Standard mode
            await ProcessTableStandardModeAsync(table, cancellationToken);
        }

        /// <summary>
        /// Run all stages sequentially
        /// </summary>
        public async Task RunAllStagesAsync()
        {
            try
            {
                await PrepareTableListAsync();
                await ProcessTablesAsync();
            }
            catch (Exception ex)
            {
                _logger($"ERROR in Run All: {ex.Message}");
                OnStatusUpdated($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stops the current operation
        /// </summary>
        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
            _logger("Stop requested");
        }

        /// <summary>
        /// Clears memory used by ALL tables (CachedData, ControlData, etc.)
        /// Call this after processing is complete to free memory
        /// Retry will always re-fetch from scratch
        /// </summary>
        public void ClearCompletedTablesMemory()
        {
            int clearedCompleted = 0;
            int clearedFailed = 0;
            long totalRowsCleared = 0;

            foreach (var table in _tables)
            {
                // Track rows being cleared
                totalRowsCleared += table.CachedData?.Rows.Count ?? 0;
                totalRowsCleared += table.ControlData?.Rows.Count ?? 0;

                // Dispose data for ALL tables - retry will re-fetch anyway
                table.CachedData?.Dispose();
                table.CachedData = null;
                table.ControlData?.Dispose();
                table.ControlData = null;

                if (table.Status == TableStatus.Inserted || table.Status == TableStatus.Excluded)
                {
                    // Also clear metadata for completed tables
                    table.CopyableFields.Clear();
                    table.FetchSql = string.Empty;
                    table.SqlTemplate = string.Empty;
                    clearedCompleted++;
                }
                else if (table.Status == TableStatus.FetchError || table.Status == TableStatus.InsertError)
                {
                    clearedFailed++;
                }
            }

            // Log memory before GC
            long memoryBefore = GC.GetTotalMemory(false) / 1024 / 1024;

            // Force garbage collection to release memory
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);

            // Log memory after GC
            long memoryAfter = GC.GetTotalMemory(true) / 1024 / 1024;

            if (clearedCompleted > 0 || clearedFailed > 0)
            {
                _logger($"Cleared memory: {clearedCompleted} completed, {clearedFailed} failed tables, {totalRowsCleared:N0} rows (Memory: {memoryBefore}MB → {memoryAfter}MB)");
            }
        }

        // ========== Helper Methods ==========

        private long GetMinRecIdFromData(DataTable data)
        {
            if (data.Rows.Count == 0 || !data.Columns.Contains("RecId"))
                return 0;

            long minRecId = long.MaxValue;
            foreach (DataRow row in data.Rows)
            {
                if (row["RecId"] != DBNull.Value)
                {
                    long recId = Convert.ToInt64(row["RecId"]);
                    if (recId < minRecId)
                        minRecId = recId;
                }
            }

            return minRecId == long.MaxValue ? 0 : minRecId;
        }

        private Dictionary<string, StrategyOverride> ParseStrategyOverrides(string overrides)
        {
            var result = new Dictionary<string, StrategyOverride>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(overrides))
                return result;

            var lines = overrides.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int lineNumber = 0;

            foreach (var line in lines)
            {
                lineNumber++;
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                try
                {
                    var parsed = ParseStrategyLine(trimmed);
                    result[parsed.TableName] = parsed;
                }
                catch (Exception ex)
                {
                    throw new Exception($"Line {lineNumber}: {ex.Message}\nLine text: {trimmed}");
                }
            }

            return result;
        }

        private StrategyOverride ParseStrategyLine(string line)
        {
            // Check for -truncate flag at the end
            bool useTruncate = false;
            string workingLine = line;

            // Check for -truncate flag
            if (workingLine.EndsWith(" -truncate", StringComparison.OrdinalIgnoreCase))
            {
                useTruncate = true;
                workingLine = workingLine.Substring(0, workingLine.Length - 10).Trim();
            }

            // Split by pipe
            var parts = workingLine.Split('|');

            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                throw new Exception("Invalid format: missing table name");

            string tableName = parts[0].Trim();

            // Case 1: TableName only (use default RecId strategy)
            if (parts.Length == 1)
            {
                return new StrategyOverride
                {
                    TableName = tableName,
                    StrategyType = CopyStrategyType.RecId,
                    RecIdCount = _config.DefaultRecordCount,
                    UseTruncate = useTruncate
                };
            }

            string part1 = parts[1].Trim();

            // Case 2: TableName|sql:... (SQL without explicit count)
            if (part1.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
            {
                return ParseSqlStrategy(tableName, part1, null, useTruncate);
            }

            // Case 3: TableName|Number (RecId strategy)
            if (int.TryParse(part1, out int count))
            {
                if (count <= 0)
                    throw new Exception("Invalid format: RecId count must be positive");

                // Check if there's a sql: part after the count
                if (parts.Length >= 3)
                {
                    string part2 = parts[2].Trim();
                    if (part2.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Case 4: TableName|Number|sql:... (SQL with explicit count)
                        return ParseSqlStrategy(tableName, part2, count, useTruncate);
                    }
                    else
                    {
                        throw new Exception($"Invalid format: unexpected '{part2}' after record count");
                    }
                }

                return new StrategyOverride
                {
                    TableName = tableName,
                    StrategyType = CopyStrategyType.RecId,
                    RecIdCount = count,
                    UseTruncate = useTruncate
                };
            }

            throw new Exception($"Invalid format: '{part1}' is not a valid strategy (expected number or 'sql:...')");
        }

        private StrategyOverride ParseSqlStrategy(string tableName, string sqlPart, int? recordCount, bool useTruncate)
        {
            // Extract SQL after "sql:" prefix
            string sql = sqlPart.Substring(4).Trim();

            if (string.IsNullOrEmpty(sql))
                throw new Exception("Invalid format: empty SQL statement");

            // Validate: must contain *
            if (!sql.Contains("*"))
                throw new Exception("SQL strategy must contain '*' for field replacement");

            return new StrategyOverride
            {
                TableName = tableName,
                StrategyType = CopyStrategyType.Sql,
                RecIdCount = recordCount,
                SqlTemplate = sql,
                UseTruncate = useTruncate
            };
        }

        private StrategyOverride GetStrategy(string tableName, Dictionary<string, StrategyOverride> overrides)
        {
            if (overrides.TryGetValue(tableName, out var strategy))
                return strategy;

            // Return default strategy
            return new StrategyOverride
            {
                TableName = tableName,
                StrategyType = CopyStrategyType.RecId,
                RecIdCount = _config.DefaultRecordCount,
                SqlTemplate = string.Empty,
                UseTruncate = false
            };
        }

        private string CombineExclusionPatterns(string tablesToExclude, string systemExcludedTables)
        {
            // Combine both exclusion lists
            var combined = new List<string>();

            if (!string.IsNullOrWhiteSpace(tablesToExclude))
                combined.Add(tablesToExclude.Trim());

            if (!string.IsNullOrWhiteSpace(systemExcludedTables))
                combined.Add(systemExcludedTables.Trim());

            return string.Join("\r\n", combined);
        }

        private List<string> GetPatterns(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new List<string>();

            return input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
        }

        private bool MatchesAnyPattern(string tableName, List<string> patterns)
        {
            if (patterns.Count == 0)
                return false;

            foreach (var pattern in patterns)
            {
                if (MatchesPattern(tableName, pattern))
                    return true;
            }

            return false;
        }

        private bool MatchesPattern(string tableName, string pattern)
        {
            // Convert wildcard pattern to regex
            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(tableName, regexPattern, RegexOptions.IgnoreCase);
        }

        private string FormatTime(int totalSeconds)
        {
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return $"{minutes}:{seconds:D2}";
        }

        private Dictionary<string, List<string>> GetExcludedFieldsMap(string fieldsToExclude)
        {
            var result = new Dictionary<string, List<string>>();
            result[""] = new List<string>(); // Global exclusions

            if (string.IsNullOrWhiteSpace(fieldsToExclude))
                return result;

            var lines = fieldsToExclude.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                if (trimmed.Contains('.'))
                {
                    // Per-table exclusion: TableName.FieldName
                    var parts = trimmed.Split('.');
                    if (parts.Length == 2)
                    {
                        string tableName = parts[0].Trim().ToUpper();
                        string fieldName = parts[1].Trim().ToUpper();

                        if (!result.ContainsKey(tableName))
                            result[tableName] = new List<string>();

                        result[tableName].Add(fieldName);
                    }
                }
                else
                {
                    // Global exclusion
                    result[""].Add(trimmed.ToUpper());
                }
            }

            return result;
        }

        private string GenerateFetchSql(string tableName, List<string> fields, StrategyOverride strategy)
        {
            string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));
            int recordCount = strategy.RecIdCount ?? _config.DefaultRecordCount;

            switch (strategy.StrategyType)
            {
                case CopyStrategyType.RecId:
                    return $"SELECT TOP ({recordCount}) {fieldList} FROM [{tableName}] ORDER BY RecId DESC";

                case CopyStrategyType.Sql:
                    // Replace parameters in SQL template
                    // Replace @sysRowVersionFilter with (1 = 1) for standard/TRUNCATE mode
                    string sql = strategy.SqlTemplate
                        .Replace("@recordCount", recordCount.ToString())
                        .Replace("*", fieldList)
                        .Replace("@sysRowVersionFilter", "(1 = 1)", StringComparison.OrdinalIgnoreCase);
                    return sql;

                default:
                    throw new Exception($"Unsupported strategy type: {strategy.StrategyType}");
            }
        }

        private void OnTablesUpdated()
        {
            TablesUpdated?.Invoke(this, _tables);
        }

        private void OnStatusUpdated(string status)
        {
            StatusUpdated?.Invoke(this, status);
        }

        private void OnTimestampsUpdated()
        {
            TimestampsUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    // Helper class for strategy parsing
    public class StrategyOverride
    {
        public string TableName { get; set; } = string.Empty;
        public CopyStrategyType StrategyType { get; set; }
        public int? RecIdCount { get; set; }      // For RecId strategy or SQL with explicit count
        public string SqlTemplate { get; set; } = string.Empty;  // For SQL strategy (raw template with * and @recordCount)
        public bool UseTruncate { get; set; }
    }

    // Comparer for SQL Server timestamps
    public class TimestampComparer : IComparer<byte[]?>
    {
        public int Compare(byte[]? x, byte[]? y)
        {
            return TimestampHelper.CompareTimestamp(x, y);
        }
    }
}
