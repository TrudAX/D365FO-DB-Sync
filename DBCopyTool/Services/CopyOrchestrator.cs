using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DBCopyTool.Models;

namespace DBCopyTool.Services
{
    public class CopyOrchestrator
    {
        private readonly AppConfiguration _config;
        private readonly Tier2DataService _tier2Service;
        private readonly AxDbDataService _axDbService;
        private readonly Action<string> _logger;

        private List<TableInfo> _tables = new List<TableInfo>();
        private CancellationTokenSource? _cancellationTokenSource;

        public event EventHandler<List<TableInfo>>? TablesUpdated;
        public event EventHandler<string>? StatusUpdated;

        public CopyOrchestrator(AppConfiguration config, Action<string> logger)
        {
            _config = config;
            _tier2Service = new Tier2DataService(config.Tier2Connection, logger);
            _axDbService = new AxDbDataService(config.AxDbConnection, logger);
            _logger = logger;
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

                // ========== LOAD SQLDICTIONARY CACHES ONCE ==========
                _logger("─────────────────────────────────────────────");
                var tier2Cache = await _tier2Service.LoadSqlDictionaryCacheAsync();
                var axDbCache = await _axDbService.LoadSqlDictionaryCacheAsync();
                _logger("─────────────────────────────────────────────");
                // ====================================================

                // Discover tables from Tier2
                _logger("Discovering tables from Tier2...");
                var discoveredTables = await _tier2Service.DiscoverTablesAsync();
                _logger($"Discovered {discoveredTables.Count} tables");

                // Get inclusion and exclusion patterns
                var inclusionPatterns = GetPatterns(_config.TablesToInclude);

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

                    // Verify ModifiedDate strategy requirements
                    if (strategy.StrategyType == CopyStrategyType.ModifiedDate ||
                        strategy.StrategyType == CopyStrategyType.ModifiedDateWithWhere)
                    {
                        if (!copyableFields.Any(f => f.Equals("MODIFIEDDATETIME", StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger($"Table {tableName} configured for ModifiedDate strategy but lacks MODIFIEDDATETIME column");

                            var errorTable = new TableInfo
                            {
                                TableName = tableName,
                                TableId = tier2TableId.Value,
                                Status = TableStatus.FetchError,
                                Error = "MODIFIEDDATETIME column not found",
                                Tier2RowCount = rowCount,
                                Tier2SizeGB = sizeGB,
                                BytesPerRow = bytesPerRow
                            };
                            _tables.Add(errorTable);
                            skipped++;
                            continue;
                        }
                    }

                    // Generate fetch SQL
                    string fetchSql = GenerateFetchSql(tableName, copyableFields, strategy);

                    // Calculate records to copy based on strategy
                    long recordsToCopy = strategy.StrategyType switch
                    {
                        CopyStrategyType.RecId or CopyStrategyType.RecIdWithWhere => strategy.RecIdCount ?? 0,
                        CopyStrategyType.All => rowCount,
                        _ => rowCount  // For ModifiedDate/Where, use Tier2RowCount as rough estimate
                    };

                    // Calculate estimated size in MB using minimum of RecordsToCopy and Tier2RowCount
                    long recordsForCalculation = Math.Min(recordsToCopy, rowCount);
                    decimal estimatedSizeMB = bytesPerRow > 0 && recordsForCalculation > 0
                        ? (decimal)bytesPerRow * recordsForCalculation / 1_000_000m
                        : 0;

                    // Create TableInfo
                    var tableInfo = new TableInfo
                    {
                        TableName = tableName,
                        TableId = tier2TableId.Value,
                        StrategyType = strategy.StrategyType,
                        StrategyValue = strategy.RecIdCount ?? strategy.DaysCount ?? 0,  // Backward compatibility
                        RecIdCount = strategy.RecIdCount,
                        DaysCount = strategy.DaysCount,
                        WhereClause = strategy.WhereClause,
                        UseTruncate = strategy.UseTruncate,
                        NoCompareFlag = strategy.NoCompareFlag,
                        Tier2RowCount = rowCount,
                        Tier2SizeGB = sizeGB,
                        BytesPerRow = bytesPerRow,
                        RecordsToCopy = recordsToCopy,
                        EstimatedSizeMB = estimatedSizeMB,
                        FetchSql = fetchSql,
                        CopyableFields = copyableFields,
                        Status = TableStatus.Pending
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
                    // Clear cached data to force re-fetch with fresh data
                    table.CachedData = null;
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
            // Parse strategy overrides from current config
            var strategyOverrides = ParseStrategyOverrides(_config.StrategyOverrides);

            // Get the strategy for this table
            var strategy = GetStrategy(table.TableName, strategyOverrides);

            // Verify ModifiedDate strategy requirements
            if (strategy.StrategyType == CopyStrategyType.ModifiedDate ||
                strategy.StrategyType == CopyStrategyType.ModifiedDateWithWhere)
            {
                if (!table.CopyableFields.Any(f => f.Equals("MODIFIEDDATETIME", StringComparison.OrdinalIgnoreCase)))
                {
                    table.Status = TableStatus.FetchError;
                    table.Error = "MODIFIEDDATETIME column not found for ModifiedDate strategy";
                    _logger($"Table {table.TableName} configured for ModifiedDate strategy but lacks MODIFIEDDATETIME column");
                    return;
                }
            }

            // Regenerate fetch SQL with new strategy
            string fetchSql = GenerateFetchSql(table.TableName, table.CopyableFields, strategy);

            // Calculate records to copy based on strategy
            long recordsToCopy = strategy.StrategyType switch
            {
                CopyStrategyType.RecId or CopyStrategyType.RecIdWithWhere => strategy.RecIdCount ?? 0,
                CopyStrategyType.All => table.Tier2RowCount,
                _ => table.Tier2RowCount  // For ModifiedDate/Where, use Tier2RowCount as rough estimate
            };

            // Calculate estimated size in MB using minimum of RecordsToCopy and Tier2RowCount
            long recordsForCalculation = Math.Min(recordsToCopy, table.Tier2RowCount);
            decimal estimatedSizeMB = table.BytesPerRow > 0 && recordsForCalculation > 0
                ? (decimal)table.BytesPerRow * recordsForCalculation / 1_000_000m
                : 0;

            // Update table with new strategy
            table.StrategyType = strategy.StrategyType;
            table.StrategyValue = strategy.RecIdCount ?? strategy.DaysCount ?? 0;  // Backward compatibility
            table.RecIdCount = strategy.RecIdCount;
            table.DaysCount = strategy.DaysCount;
            table.WhereClause = strategy.WhereClause;
            table.UseTruncate = strategy.UseTruncate;
            table.NoCompareFlag = strategy.NoCompareFlag;
            table.RecordsToCopy = recordsToCopy;
            table.EstimatedSizeMB = estimatedSizeMB;
            table.FetchSql = fetchSql;

            _logger($"Re-applied strategy for {table.TableName}: {table.StrategyDisplay}");
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
            table.CachedData = null;
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
        /// Process a single table: fetch → insert → clear memory
        /// </summary>
        private async Task ProcessSingleTableAsync(TableInfo table, CancellationToken cancellationToken)
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
                    table.DaysCount,
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

                // STAGE 2: INSERT (includes delete and insert operations)
                table.Status = TableStatus.Inserting;
                OnTablesUpdated();

                await _axDbService.InsertDataAsync(table, cancellationToken);

                _logger($"Deleted {table.TableName}: {table.DeleteTimeSeconds:F2}s, Inserted: {table.InsertTimeSeconds:F2}s");

                // STAGE 3: CLEANUP MEMORY (on success only)
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
                // Keep CachedData for potential retry
                table.Status = TableStatus.FetchError;
                table.Error = "Cancelled";
                _logger($"Cancelled {table.TableName}");
                throw;
            }
            catch (Exception ex)
            {
                // Determine which stage failed based on current status
                if (table.Status == TableStatus.Fetching)
                {
                    table.Status = TableStatus.FetchError;
                    table.CachedData = null; // No data to keep
                }
                else if (table.Status == TableStatus.Inserting)
                {
                    table.Status = TableStatus.InsertError;
                    // Keep CachedData for retry
                }

                table.Error = ex.Message;
                _logger($"ERROR processing {table.TableName}: {ex.Message}");
            }
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
            // Check for -nocompare and -truncate flags at the end
            bool noCompare = false;
            bool useTruncate = false;
            string workingLine = line;

            // Check for -nocompare flag
            if (workingLine.EndsWith(" -nocompare", StringComparison.OrdinalIgnoreCase))
            {
                noCompare = true;
                workingLine = workingLine.Substring(0, workingLine.Length - 11).Trim();
            }

            // Check for -truncate flag
            if (workingLine.EndsWith(" -truncate", StringComparison.OrdinalIgnoreCase))
            {
                useTruncate = true;
                workingLine = workingLine.Substring(0, workingLine.Length - 10).Trim();
            }

            // Check again for -nocompare (in case order is -truncate -nocompare)
            if (workingLine.EndsWith(" -nocompare", StringComparison.OrdinalIgnoreCase))
            {
                noCompare = true;
                workingLine = workingLine.Substring(0, workingLine.Length - 11).Trim();
            }

            // Split by pipe
            var parts = workingLine.Split('|');

            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                throw new Exception("Invalid format: missing table name");

            string tableName = parts[0].Trim();
            string sourceStrategy = parts.Length > 1 ? parts[1].Trim() : "";
            string whereClause = "";

            // Parse WHERE clause from any position after table name
            for (int i = 2; i < parts.Length; i++)
            {
                if (parts[i].Trim().StartsWith("where:", StringComparison.OrdinalIgnoreCase))
                {
                    whereClause = parts[i].Trim().Substring(6).Trim();
                    if (string.IsNullOrEmpty(whereClause))
                        throw new Exception("Invalid format: empty WHERE condition");
                    break;
                }
            }

            // Parse source strategy
            CopyStrategyType strategyType;
            int? recIdCount = null;
            int? daysCount = null;

            if (string.IsNullOrEmpty(sourceStrategy))
            {
                // Default strategy
                strategyType = string.IsNullOrEmpty(whereClause) ? CopyStrategyType.RecId : CopyStrategyType.RecIdWithWhere;
                recIdCount = _config.DefaultRecordCount;
            }
            else if (sourceStrategy.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(whereClause))
                    throw new Exception("Invalid format: 'all' cannot be combined with WHERE clause");
                strategyType = CopyStrategyType.All;
                useTruncate = true; // 'all' implies truncate
            }
            else if (sourceStrategy.StartsWith("days:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(sourceStrategy.Substring(5), out int days) || days <= 0)
                    throw new Exception($"Invalid format: '{sourceStrategy}' is not a valid days strategy");

                daysCount = days;
                strategyType = string.IsNullOrEmpty(whereClause) ? CopyStrategyType.ModifiedDate : CopyStrategyType.ModifiedDateWithWhere;
            }
            else if (sourceStrategy.StartsWith("where:", StringComparison.OrdinalIgnoreCase))
            {
                // where: in second position
                whereClause = sourceStrategy.Substring(6).Trim();
                if (string.IsNullOrEmpty(whereClause))
                    throw new Exception("Invalid format: empty WHERE condition");
                strategyType = CopyStrategyType.Where;
            }
            else if (int.TryParse(sourceStrategy, out int count))
            {
                if (count <= 0)
                    throw new Exception($"Invalid format: RecId count must be positive");

                recIdCount = count;
                strategyType = string.IsNullOrEmpty(whereClause) ? CopyStrategyType.RecId : CopyStrategyType.RecIdWithWhere;
            }
            else if (string.IsNullOrEmpty(sourceStrategy) && useTruncate)
            {
                throw new Exception("Invalid format: missing source strategy before -truncate");
            }
            else
            {
                throw new Exception($"Invalid format: '{sourceStrategy}' is not a valid strategy");
            }

            return new StrategyOverride
            {
                TableName = tableName,
                StrategyType = strategyType,
                RecIdCount = recIdCount,
                DaysCount = daysCount,
                WhereClause = whereClause,
                UseTruncate = useTruncate,
                NoCompareFlag = noCompare
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
                DaysCount = null,
                WhereClause = string.Empty,
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
            string whereClause = string.IsNullOrEmpty(strategy.WhereClause) ? "" : $" WHERE {strategy.WhereClause}";

            switch (strategy.StrategyType)
            {
                case CopyStrategyType.RecId:
                    return $"SELECT TOP ({strategy.RecIdCount}) {fieldList} FROM [{tableName}] ORDER BY RecId DESC";

                case CopyStrategyType.ModifiedDate:
                    return $"SELECT {fieldList} FROM [{tableName}] WHERE [MODIFIEDDATETIME] > @CutoffDate";

                case CopyStrategyType.Where:
                    return $"SELECT {fieldList} FROM [{tableName}]{whereClause}";

                case CopyStrategyType.RecIdWithWhere:
                    return $"SELECT TOP ({strategy.RecIdCount}) {fieldList} FROM [{tableName}]{whereClause} ORDER BY RecId DESC";

                case CopyStrategyType.ModifiedDateWithWhere:
                    return $"SELECT {fieldList} FROM [{tableName}] WHERE [MODIFIEDDATETIME] > @CutoffDate AND ({strategy.WhereClause})";

                case CopyStrategyType.All:
                    return $"SELECT {fieldList} FROM [{tableName}]";

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
    }

    // Helper class for strategy parsing
    public class StrategyOverride
    {
        public string TableName { get; set; } = string.Empty;
        public CopyStrategyType StrategyType { get; set; }
        public int? RecIdCount { get; set; }
        public int? DaysCount { get; set; }
        public string WhereClause { get; set; } = string.Empty;
        public bool UseTruncate { get; set; }
        public bool NoCompareFlag { get; set; }
    }
}
