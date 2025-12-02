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
        /// Stage 2: Fetch Data
        /// </summary>
        public async Task GetDataAsync()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger("─────────────────────────────────────────────");
                _logger("Starting Fetch Data...");

                var pendingTables = _tables.Where(t => t.Status == TableStatus.Pending).ToList();
                if (pendingTables.Count == 0)
                {
                    _logger("No pending tables to fetch");
                    return;
                }

                int completed = 0;
                int failed = 0;
                var semaphore = new SemaphoreSlim(_config.ParallelFetchConnections);

                var tasks = pendingTables.Select(async table =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await FetchTableDataAsync(table, cancellationToken);
                        if (table.Status == TableStatus.Fetched)
                            Interlocked.Increment(ref completed);
                        else
                            Interlocked.Increment(ref failed);

                        OnStatusUpdated($"Stage 2/3: Fetch Data - {completed + failed}/{pendingTables.Count} tables");
                    }
                    finally
                    {
                        semaphore.Release();
                        OnTablesUpdated();
                    }
                });

                await Task.WhenAll(tasks);

                _logger($"Fetched {completed} tables, {failed} failed");
                OnStatusUpdated($"Fetched {completed} tables, {failed} failed");
            }
            catch (OperationCanceledException)
            {
                _logger("Fetch Data cancelled");
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
        /// Stage 3: Insert Data
        /// </summary>
        public async Task InsertDataAsync()
        {
            await InsertDataInternalAsync(false);
        }

        /// <summary>
        /// Stage 4: Retry Failed
        /// </summary>
        public async Task InsertFailedAsync()
        {
            await InsertDataInternalAsync(true);
        }

        /// <summary>
        /// Internal method to insert data (for both Stage 3 and Stage 4)
        /// </summary>
        private async Task InsertDataInternalAsync(bool retryOnly)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                _logger("─────────────────────────────────────────────");
                string stageName = retryOnly ? "Retry Failed" : "Insert Data";
                _logger($"Starting {stageName}...");

                var tablesToInsert = retryOnly
                    ? _tables.Where(t => t.Status == TableStatus.InsertError).ToList()
                    : _tables.Where(t => t.Status == TableStatus.Fetched).ToList();

                if (tablesToInsert.Count == 0)
                {
                    _logger($"No tables to insert");
                    return;
                }

                int completed = 0;
                int failed = 0;
                var semaphore = new SemaphoreSlim(_config.ParallelInsertConnections);

                var tasks = tablesToInsert.Select(async table =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        await InsertTableDataAsync(table, cancellationToken);
                        if (table.Status == TableStatus.Inserted)
                            Interlocked.Increment(ref completed);
                        else
                            Interlocked.Increment(ref failed);

                        OnStatusUpdated($"Stage 3/3: Insert Data - {completed + failed}/{tablesToInsert.Count} tables");
                    }
                    finally
                    {
                        semaphore.Release();
                        OnTablesUpdated();
                    }
                });

                await Task.WhenAll(tasks);

                _logger($"Inserted {completed} tables, {failed} failed");
                OnStatusUpdated($"Inserted {completed} tables, {failed} failed");
            }
            catch (OperationCanceledException)
            {
                _logger("Insert Data cancelled");
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
        /// Run all stages sequentially
        /// </summary>
        public async Task RunAllStagesAsync()
        {
            try
            {
                await PrepareTableListAsync();
                await GetDataAsync();
                await InsertDataAsync();
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

        private async Task FetchTableDataAsync(TableInfo table, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            table.Status = TableStatus.Fetching;
            OnTablesUpdated();

            try
            {
                // Use pre-generated SQL from FetchSql property
                DataTable data = await _tier2Service.FetchDataBySqlAsync(
                    table.TableName,
                    table.FetchSql,
                    table.DaysCount,
                    cancellationToken);

                table.CachedData = data;
                table.RecordsFetched = data.Rows.Count;
                table.MinRecId = GetMinRecIdFromData(data);
                table.FetchTimeSeconds = (decimal)stopwatch.Elapsed.TotalSeconds;

                // Update records to copy and estimated size with actual fetched count
                table.RecordsToCopy = data.Rows.Count;
                long recordsForCalculation = Math.Min(data.Rows.Count, table.Tier2RowCount);
                table.EstimatedSizeMB = table.BytesPerRow > 0 && recordsForCalculation > 0
                    ? (decimal)table.BytesPerRow * recordsForCalculation / 1_000_000m
                    : 0;

                table.Status = TableStatus.Fetched;

                _logger($"Fetched {table.TableName}: {table.RecordsFetched} records in {table.FetchTimeSeconds:F2}s");
            }
            catch (Exception ex)
            {
                table.Status = TableStatus.FetchError;
                table.Error = ex.Message;
                _logger($"ERROR fetching {table.TableName}: {ex.Message}");
            }
        }

        private async Task InsertTableDataAsync(TableInfo table, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            table.Status = TableStatus.Inserting;
            OnTablesUpdated();

            try
            {
                await _axDbService.InsertDataAsync(table, cancellationToken);

                table.InsertTimeSeconds = (decimal)stopwatch.Elapsed.TotalSeconds;
                table.Status = TableStatus.Inserted;
                table.Error = string.Empty;

                _logger($"Inserted {table.TableName}: {table.RecordsFetched} records in {table.InsertTimeSeconds:F2}s");
            }
            catch (Exception ex)
            {
                table.Status = TableStatus.InsertError;
                table.Error = ex.Message;
                _logger($"ERROR inserting {table.TableName}: {ex.Message}");
            }
        }

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

            if (line.EndsWith(" -truncate", StringComparison.OrdinalIgnoreCase))
            {
                useTruncate = true;
                workingLine = line.Substring(0, line.Length - 10).Trim();
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
    }
}
