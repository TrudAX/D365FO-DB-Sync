using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using DBSyncTool.Models;

namespace DBSyncTool.Services
{
    public class AxDbDataService
    {
        private const int DELETE_BATCH_SIZE = 5000;
        private const int SEQUENCE_GAP = 10000;

        private readonly ConnectionSettings _connectionSettings;
        private readonly string _connectionString;
        private readonly Action<string> _logger;

        public AxDbDataService(ConnectionSettings connectionSettings, Action<string> logger)
        {
            _connectionSettings = connectionSettings;
            _connectionString = connectionSettings.BuildConnectionString(isAzure: false);
            _logger = logger;
        }

        /// <summary>
        /// Gets the connection string for AxDB
        /// </summary>
        public string GetConnectionString() => _connectionString;

        /// <summary>
        /// Tests the connection to AxDB database
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection.State == ConnectionState.Open;
        }

        /// <summary>
        /// Gets the TableID for a table from SQLDICTIONARY
        /// </summary>
        public async Task<int?> GetTableIdAsync(string tableName)
        {
            const string query = @"
                SELECT TableID
                FROM SQLDICTIONARY
                WHERE UPPER(name) = UPPER(@TableName)
                  AND FIELDID = 0";

            _logger($"[AxDB SQL] Getting TableID for {tableName}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TableName", tableName);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            var result = await command.ExecuteScalarAsync();

            return result != null ? Convert.ToInt32(result) : null;
        }

        /// <summary>
        /// Gets the field names for a table from SQLDICTIONARY
        /// </summary>
        public async Task<List<string>> GetTableFieldsAsync(int tableId)
        {
            const string query = @"
                SELECT SQLName
                FROM SQLDICTIONARY
                WHERE TableID = @TableId
                  AND FIELDID <> 0";

            _logger($"[AxDB SQL] Getting fields for TableID {tableId}");

            var fields = new List<string>();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TableId", tableId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                fields.Add(reader.GetString(0));
            }

            return fields;
        }

        /// <summary>
        /// Inserts data into a table using SqlBulkCopy
        /// Handles deletes, trigger disabling, bulk insert, trigger enabling, and sequence update
        /// </summary>
        public async Task<int> InsertDataAsync(TableInfo tableInfo, CancellationToken cancellationToken)
        {
            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return 0;

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            DataTable? filteredDataToDispose = null;  // Track filtered DataTable for disposal - declared outside try for catch block access

            try
            {
                _logger($"[AxDB] Starting insert for table {tableInfo.TableName} ({tableInfo.CachedData.Rows.Count} rows)");

                // Determine if we should use delta comparison
                bool shouldCompare = ShouldUseComparison(tableInfo);

                ComparisonResult? comparison = null;
                DataTable dataToInsert = tableInfo.CachedData;
                HashSet<long> recIdsToDelete = new HashSet<long>();

                if (shouldCompare)
                {
                    var compareStopwatch = Stopwatch.StartNew();

                    // Get comparison context (intersection of Tier2 and AxDB columns)
                    var axDbColumns = await GetAxDbComparisonColumnsAsync(
                        tableInfo.TableName, connection, null, cancellationToken);
                    var context = BuildComparisonContext(tableInfo.CachedData, axDbColumns);

                    if (!context.CanCompare)
                    {
                        _logger($"[AxDB] {tableInfo.TableName}: RECVERSION not in both databases, using full sync");
                        shouldCompare = false;
                    }
                    else
                    {
                        // Log comparison mode
                        string mode = context.IsFallbackMode
                            ? "RECVERSION only (RECVERSION=1 excluded)"
                            : $"RECVERSION + {(context.HasCreatedDateTime ? "CREATEDDATETIME " : "")}{(context.HasModifiedDateTime ? "MODIFIEDDATETIME" : "")}".Trim();
                        _logger($"[AxDB] {tableInfo.TableName}: Comparison mode: {mode}");

                        // Fetch version map from AxDB
                        long minRecId = GetMinRecId(tableInfo.CachedData);
                        var axDbVersions = await GetAxDbVersionMapAsync(
                            tableInfo.TableName, minRecId, context, connection, null, cancellationToken);

                        // Compare records
                        comparison = CompareRecords(tableInfo.CachedData, axDbVersions, context, tableInfo.StoredMaxRecId);

                        compareStopwatch.Stop();
                        tableInfo.CompareTimeSeconds = (decimal)compareStopwatch.Elapsed.TotalSeconds;
                        tableInfo.ComparisonUsed = true;
                        tableInfo.UnchangedCount = comparison.UnchangedRecIds.Count;
                        tableInfo.ModifiedCount = comparison.ModifiedRecIds.Count;
                        tableInfo.NewInTier2Count = comparison.NewRecIds.Count;
                        tableInfo.DeletedFromAxDbCount = comparison.DeletedRecIds.Count;

                        _logger($"[AxDB] Compared {tableInfo.TableName}: {comparison.UnchangedRecIds.Count:N0} unchanged, " +
                               $"{comparison.ModifiedRecIds.Count:N0} modified, {comparison.NewRecIds.Count:N0} new, " +
                               $"{comparison.DeletedRecIds.Count:N0} deleted in {tableInfo.CompareTimeSeconds:F2}s");

                        // Check if anything needs to be done
                        if (comparison.ModifiedRecIds.Count == 0 &&
                            comparison.NewRecIds.Count == 0 &&
                            comparison.DeletedRecIds.Count == 0)
                        {
                            _logger($"[AxDB] {tableInfo.TableName}: All records unchanged, skipping delete/insert");
                            tableInfo.DeleteTimeSeconds = 0;
                            tableInfo.InsertTimeSeconds = 0;

                            // Still update sequence with gap
                            await UpdateSequenceAsync(tableInfo, connection, null, cancellationToken);

                            return 0;
                        }

                        // Prepare for delete and insert
                        recIdsToDelete = new HashSet<long>(comparison.ModifiedRecIds);
                        recIdsToDelete.UnionWith(comparison.DeletedRecIds);

                        var recIdsToInsert = new HashSet<long>(comparison.ModifiedRecIds);
                        recIdsToInsert.UnionWith(comparison.NewRecIds);

                        filteredDataToDispose = FilterDataTableByRecIds(tableInfo.CachedData, recIdsToInsert);
                        dataToInsert = filteredDataToDispose;

                        _logger($"[AxDB] {tableInfo.TableName}: Will delete {recIdsToDelete.Count:N0}, insert {dataToInsert.Rows.Count:N0}");

                        // OPTIMIZATION: If too many changes, use TRUNCATE instead of delta DELETE
                        // Calculate change percentage based on total fetched records
                        long totalChanged = comparison.ModifiedRecIds.Count + comparison.NewRecIds.Count + comparison.DeletedRecIds.Count;
                        double changePercent = tableInfo.CachedData.Rows.Count > 0
                            ? (double)totalChanged / tableInfo.CachedData.Rows.Count * 100
                            : 0;

                        // Get truncate threshold from table (set by orchestrator from config)
                        double truncateThreshold = tableInfo.TruncateThresholdPercent > 0
                            ? tableInfo.TruncateThresholdPercent
                            : 40.0; // Default fallback

                        if (changePercent >= truncateThreshold)
                        {
                            _logger($"[AxDB] {tableInfo.TableName}: Change percentage {changePercent:F1}% >= threshold {truncateThreshold}%, switching to TRUNCATE mode");

                            // Switch to full table mode - clear comparison flag and use all data
                            shouldCompare = false;
                            comparison = null;
                            recIdsToDelete.Clear();
                            dataToInsert = tableInfo.CachedData;  // Use full dataset
                            filteredDataToDispose?.Dispose();  // Dispose filtered copy since we're not using it
                            filteredDataToDispose = null;
                        }
                    }
                }

                // Additional check: If copying entire table, delete records below MinRecId
                // This handles the case where AxDB has old records with RecIds below Tier2 range
                bool shouldDeleteBelowMinRecId = false;
                if (shouldCompare && comparison != null &&
                    tableInfo.Tier2RowCount > 0 &&
                    tableInfo.RecordsToCopy > 0 &&
                    tableInfo.Tier2RowCount <= tableInfo.RecordsToCopy)
                {
                    shouldDeleteBelowMinRecId = true;
                }

                // Step 1: Disable triggers (BEFORE any DELETE or INSERT operations)
                string disableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] DISABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {disableTriggersSql}");
                await ExecuteNonQueryAsync(disableTriggersSql, connection, null);

                // Step 2: Delete existing records
                var deleteStopwatch = Stopwatch.StartNew();

                if (shouldCompare && comparison != null)
                {
                    // If copying entire table, first delete records below MinRecId
                    if (shouldDeleteBelowMinRecId)
                    {
                        long minRecId = GetMinRecId(tableInfo.CachedData);
                        await DeleteBelowMinRecIdAsync(tableInfo.TableName, minRecId, connection, null, cancellationToken);
                    }

                    // Delta delete: only modified + deleted RecIds
                    await DeleteByRecIdListAsync(tableInfo.TableName, recIdsToDelete, connection, null, cancellationToken);
                }
                else
                {
                    // Full delete: current behavior based on strategy
                    await DeleteExistingRecordsAsync(tableInfo, connection, null, cancellationToken);
                }

                deleteStopwatch.Stop();
                tableInfo.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;

                // Step 3: Bulk insert data
                _logger($"[AxDB] Bulk inserting {dataToInsert.Rows.Count} rows into {tableInfo.TableName}");
                var insertStopwatch = Stopwatch.StartNew();

                if (dataToInsert.Rows.Count > 0)
                {
                    using (var bulkCopy = new SqlBulkCopy(connection))
                    {
                        bulkCopy.DestinationTableName = tableInfo.TableName;
                        bulkCopy.BatchSize = 10000;
                        bulkCopy.BulkCopyTimeout = _connectionSettings.CommandTimeout;

                        // Map columns
                        foreach (DataColumn column in dataToInsert.Columns)
                        {
                            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                        }

                        await bulkCopy.WriteToServerAsync(dataToInsert, cancellationToken);
                    }
                }

                insertStopwatch.Stop();
                tableInfo.InsertTimeSeconds = (decimal)insertStopwatch.Elapsed.TotalSeconds;

                // Step 4: Enable triggers (always, even if errors occur)
                string enableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {enableTriggersSql}");
                await ExecuteNonQueryAsync(enableTriggersSql, connection, null);

                // Step 5: Update sequence
                await UpdateSequenceAsync(tableInfo, connection, null, cancellationToken);

                int rowCount = dataToInsert.Rows.Count;

                // Dispose filtered DataTable if we created one
                filteredDataToDispose?.Dispose();

                return rowCount;
            }
            catch
            {
                // Always try to re-enable triggers on error
                try
                {
                    await ExecuteNonQueryAsync($"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL", connection, null);
                }
                catch
                {
                    // Ignore errors when re-enabling triggers
                }

                // Dispose filtered DataTable even on error
                filteredDataToDispose?.Dispose();

                throw;
            }
        }

        /// <summary>
        /// Deletes existing records based on the copy strategy and cleanup rules
        /// </summary>
        private async Task DeleteExistingRecordsAsync(TableInfo tableInfo, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
        {
            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return;

            // Optimization: If Tier2 has fewer rows than we're configured to copy, use TRUNCATE
            // This means we're copying the entire source table, so TRUNCATE is much faster than DELETE
            if (tableInfo.Tier2RowCount > 0 &&
                tableInfo.RecordsToCopy > 0 &&
                tableInfo.Tier2RowCount <= tableInfo.RecordsToCopy &&
                !tableInfo.UseTruncate)
            {
                _logger($"[AxDB] Optimization: Tier2 has {tableInfo.Tier2RowCount} rows, copying {tableInfo.RecordsToCopy} - using TRUNCATE instead of DELETE");

                string truncateQuery = $"TRUNCATE TABLE [{tableInfo.TableName}]";
                _logger($"[AxDB SQL] {truncateQuery}");

                using var command = new SqlCommand(truncateQuery, connection, transaction);
                command.CommandTimeout = _connectionSettings.CommandTimeout;
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            // If UseTruncate flag is set, always truncate
            if (tableInfo.UseTruncate)
            {
                string truncateQuery = $"TRUNCATE TABLE [{tableInfo.TableName}]";
                _logger($"[AxDB SQL] Truncating table: {truncateQuery}");

                using var command = new SqlCommand(truncateQuery, connection, transaction);
                command.CommandTimeout = _connectionSettings.CommandTimeout;
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            // Apply cleanup rules based on strategy type
            switch (tableInfo.StrategyType)
            {
                case CopyStrategyType.RecId:
                case CopyStrategyType.Sql:
                    // RecId/Sql: DELETE WHERE RecId >= @MinRecId
                    await DeleteByRecIdAsync(tableInfo, connection, transaction, cancellationToken);
                    break;

                default:
                    throw new Exception($"Unsupported strategy type: {tableInfo.StrategyType}");
            }
        }

        private async Task DeleteByRecIdAsync(TableInfo tableInfo, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
        {
            long minRecId = GetMinRecId(tableInfo.CachedData!);
            string deleteQuery = $"DELETE FROM [{tableInfo.TableName}] WHERE RecId >= @MinRecId";
            _logger($"[AxDB SQL] Deleting by RecId: {deleteQuery} (MinRecId={minRecId})");

            using var command = new SqlCommand(deleteQuery, connection, transaction);
            command.Parameters.AddWithValue("@MinRecId", minRecId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }


        /// <summary>
        /// Updates the sequence for a table if needed
        /// </summary>
        private async Task UpdateSequenceAsync(TableInfo tableInfo, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
        {
            // Get max RecId from the table
            string maxRecIdQuery = $"SELECT MAX(RecId) FROM [{tableInfo.TableName}]";
            using var command1 = new SqlCommand(maxRecIdQuery, connection, transaction);
            command1.CommandTimeout = _connectionSettings.CommandTimeout;
            var maxRecIdResult = await command1.ExecuteScalarAsync(cancellationToken);

            if (maxRecIdResult == null || maxRecIdResult == DBNull.Value)
            {
                _logger($"[AxDB] {tableInfo.TableName}: No records found, skipping sequence update");
                return;
            }

            long maxRecId = Convert.ToInt64(maxRecIdResult);

            // Get current sequence value (use AxDB TableId for local sequences)
            string sequenceName = $"SEQ_{tableInfo.AxDbTableId}";
            string currentSeqQuery = "SELECT CAST(current_value AS BIGINT) FROM sys.sequences WHERE name = @SequenceName";

            using var command2 = new SqlCommand(currentSeqQuery, connection, transaction);
            command2.Parameters.AddWithValue("@SequenceName", sequenceName);
            command2.CommandTimeout = _connectionSettings.CommandTimeout;
            var currentSeqResult = await command2.ExecuteScalarAsync(cancellationToken);

            if (currentSeqResult == null || currentSeqResult == DBNull.Value)
            {
                _logger($"[AxDB] {tableInfo.TableName}: Sequence {sequenceName} not found in sys.sequences (AxDbTableId={tableInfo.AxDbTableId}), skipping sequence update");
                return;
            }

            long currentSeq = Convert.ToInt64(currentSeqResult);

            // Calculate new sequence value with gap
            long newSeq = Math.Max(maxRecId, currentSeq) + SEQUENCE_GAP;

            string updateSeqQuery = $"ALTER SEQUENCE [{sequenceName}] RESTART WITH {newSeq}";
            _logger($"[AxDB SQL] {updateSeqQuery}");

            using var command3 = new SqlCommand(updateSeqQuery, connection, transaction);
            command3.CommandTimeout = _connectionSettings.CommandTimeout;
            await command3.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>
        /// Executes a non-query command
        /// </summary>
        private async Task ExecuteNonQueryAsync(string query, SqlConnection connection, SqlTransaction? transaction)
        {
            using var command = new SqlCommand(query, connection, transaction);
            command.CommandTimeout = _connectionSettings.CommandTimeout;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Loads entire SQLDICTIONARY into memory cache for fast lookups
        /// This dramatically reduces query count from ~4000 to 1 during PrepareTableList
        /// </summary>
        public async Task<SqlDictionaryCache> LoadSqlDictionaryCacheAsync(string? specificTableName = null)
        {
            // Build query with optional table name filter
            // NOTE: We must filter by TableID, not by name, because the name column contains
            // table names when FIELDID=0 and field names when FIELDID<>0
            string tableFilter = !string.IsNullOrWhiteSpace(specificTableName)
                ? "WHERE TableID = (SELECT TableID FROM SQLDICTIONARY WHERE UPPER(name) = @TableName AND FIELDID = 0)"
                : "";

            string query = $@"
                SELECT name, TableID, FIELDID, SQLName
                FROM SQLDICTIONARY
                {tableFilter}
                ORDER BY TableID, FIELDID";

            _logger("[AxDB] Loading SQLDICTIONARY cache...");
            _logger($"[AxDB SQL] {query}");

            var cache = new SqlDictionaryCache();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            // Add parameter if filtering by specific table
            if (!string.IsNullOrWhiteSpace(specificTableName))
            {
                command.Parameters.AddWithValue("@TableName", specificTableName);
            }

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                int tableId = reader.GetInt32(1);
                int fieldId = reader.GetInt32(2);
                string sqlName = reader.GetString(3);

                if (fieldId == 0)
                {
                    // This is a table entry
                    cache.TableNameToId[name.ToUpper()] = tableId;
                }
                else
                {
                    // This is a field entry
                    if (!cache.TableIdToFields.ContainsKey(tableId))
                    {
                        cache.TableIdToFields[tableId] = new List<string>();
                    }
                    cache.TableIdToFields[tableId].Add(sqlName);
                }
            }

            _logger($"[AxDB] Loaded SQLDICTIONARY cache: {cache.GetStats()}");

            return cache;
        }

        /// <summary>
        /// Check if DataTable has a column (case-insensitive)
        /// </summary>
        private bool HasColumn(DataTable table, string columnName)
        {
            foreach (DataColumn col in table.Columns)
            {
                if (col.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get column name with correct case from DataTable
        /// </summary>
        private string? GetColumnName(DataTable table, string columnName)
        {
            foreach (DataColumn col in table.Columns)
            {
                if (col.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return col.ColumnName;
            }
            return null;
        }

        /// <summary>
        /// Query AxDB to determine which comparison columns exist
        /// </summary>
        private async Task<HashSet<string>> GetAxDbComparisonColumnsAsync(
            string tableName,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string query = @"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @TableName
                  AND UPPER(COLUMN_NAME) IN ('RECVERSION', 'CREATEDDATETIME', 'MODIFIEDDATETIME')";

            using var command = new SqlCommand(query, connection, transaction);
            command.Parameters.AddWithValue("@TableName", tableName);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }

        /// <summary>
        /// Build comparison context from Tier2 data and AxDB columns
        /// </summary>
        private ComparisonContext BuildComparisonContext(DataTable tier2Data, HashSet<string> axDbColumns)
        {
            var context = new ComparisonContext();

            // Check intersection for each comparison field
            context.HasRecVersion = HasColumn(tier2Data, "RECVERSION") &&
                                    axDbColumns.Contains("RECVERSION");

            context.HasCreatedDateTime = HasColumn(tier2Data, "CREATEDDATETIME") &&
                                          axDbColumns.Contains("CREATEDDATETIME");

            context.HasModifiedDateTime = HasColumn(tier2Data, "MODIFIEDDATETIME") &&
                                           axDbColumns.Contains("MODIFIEDDATETIME");

            return context;
        }

        /// <summary>
        /// Determines if delta comparison should be used for this table
        /// </summary>
        private bool ShouldUseComparison(TableInfo tableInfo)
        {
            // Skip if truncating (no point comparing)
            if (tableInfo.UseTruncate)
                return false;

            // Skip if no data
            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return false;

            // Skip if RECVERSION column not present
            if (!HasColumn(tableInfo.CachedData, "RECVERSION"))
            {
                _logger($"[AxDB] {tableInfo.TableName}: RECVERSION not found, using full sync");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Fetch RecId and comparison fields from AxDB
        /// </summary>
        private async Task<Dictionary<long, AxDbRecordVersion>> GetAxDbVersionMapAsync(
            string tableName,
            long minRecId,
            ComparisonContext context,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<long, AxDbRecordVersion>();

            // Build column list based on context
            var columns = new List<string> { "RecId", "RECVERSION" };
            if (context.HasCreatedDateTime)
                columns.Add("CREATEDDATETIME");
            if (context.HasModifiedDateTime)
                columns.Add("MODIFIEDDATETIME");

            string columnList = string.Join(", ", columns);
            string query = $"SELECT {columnList} FROM [{tableName}] WHERE RecId >= @MinRecId";

            _logger($"[AxDB SQL] Fetching version map: {query} (MinRecId={minRecId})");

            using var command = new SqlCommand(query, connection, transaction);
            command.Parameters.AddWithValue("@MinRecId", minRecId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long recId = reader.GetInt64(0);
                var versionInfo = new AxDbRecordVersion
                {
                    RecVersion = reader.GetInt32(1),
                    CreatedDateTime = context.HasCreatedDateTime ? reader.GetValue(2) : null,
                    ModifiedDateTime = context.HasModifiedDateTime
                        ? reader.GetValue(context.HasCreatedDateTime ? 3 : 2)
                        : null
                };
                result[recId] = versionInfo;
            }

            _logger($"[AxDB] Fetched {result.Count:N0} records for comparison");
            return result;
        }

        /// <summary>
        /// Compare Tier2 data with AxDB version map
        /// </summary>
        private ComparisonResult CompareRecords(
            DataTable tier2Data,
            Dictionary<long, AxDbRecordVersion> axDbVersions,
            ComparisonContext context,
            long? maxTransferredRecId = null)
        {
            var result = new ComparisonResult();
            var tier2RecIds = new HashSet<long>();

            // Get column names with correct case
            string recIdCol = GetColumnName(tier2Data, "RECID") ?? "RECID";
            string recVersionCol = GetColumnName(tier2Data, "RECVERSION") ?? "RECVERSION";
            string? createdDateTimeCol = context.HasCreatedDateTime
                ? GetColumnName(tier2Data, "CREATEDDATETIME") : null;
            string? modifiedDateTimeCol = context.HasModifiedDateTime
                ? GetColumnName(tier2Data, "MODIFIEDDATETIME") : null;

            foreach (DataRow row in tier2Data.Rows)
            {
                if (row[recIdCol] == DBNull.Value)
                    continue;

                long recId = Convert.ToInt64(row[recIdCol]);
                tier2RecIds.Add(recId);

                // Check if RecId exists in AxDB
                if (!axDbVersions.TryGetValue(recId, out var axDbVersion))
                {
                    result.NewRecIds.Add(recId);
                    continue;
                }

                // Get Tier2 values
                int tier2RecVersion = Convert.ToInt32(row[recVersionCol]);

                // Fallback mode: RecVersion=1 optimization using MaxRecId
                if (context.IsFallbackMode && tier2RecVersion == 1)
                {
                    // MaxRecId optimization: if both have RecVersion=1 AND RecId <= stored max
                    if (axDbVersion.RecVersion == 1 &&
                        maxTransferredRecId.HasValue &&
                        recId <= maxTransferredRecId.Value)
                    {
                        result.UnchangedRecIds.Add(recId);
                        continue;
                    }
                    // Otherwise treat as modified (existing behavior)
                    result.ModifiedRecIds.Add(recId);
                    continue;
                }

                // Compare all available fields
                bool allMatch = true;

                // Compare RECVERSION
                if (tier2RecVersion != axDbVersion.RecVersion)
                {
                    allMatch = false;
                }

                // Compare CREATEDDATETIME if available
                if (allMatch && context.HasCreatedDateTime && createdDateTimeCol != null)
                {
                    object tier2Value = row[createdDateTimeCol];
                    object? axDbValue = axDbVersion.CreatedDateTime;
                    if (!ValuesEqual(tier2Value, axDbValue))
                    {
                        allMatch = false;
                    }
                }

                // Compare MODIFIEDDATETIME if available
                if (allMatch && context.HasModifiedDateTime && modifiedDateTimeCol != null)
                {
                    object tier2Value = row[modifiedDateTimeCol];
                    object? axDbValue = axDbVersion.ModifiedDateTime;
                    if (!ValuesEqual(tier2Value, axDbValue))
                    {
                        allMatch = false;
                    }
                }

                if (allMatch)
                {
                    result.UnchangedRecIds.Add(recId);
                }
                else
                {
                    result.ModifiedRecIds.Add(recId);
                }
            }

            // Find deleted records (in AxDB but not in Tier2 set)
            foreach (var axDbRecId in axDbVersions.Keys)
            {
                if (!tier2RecIds.Contains(axDbRecId))
                {
                    result.DeletedRecIds.Add(axDbRecId);
                }
            }

            return result;
        }

        /// <summary>
        /// Compare two values for equality (handles DBNull and null)
        /// </summary>
        private bool ValuesEqual(object? value1, object? value2)
        {
            // Both null or DBNull
            if ((value1 == null || value1 == DBNull.Value) &&
                (value2 == null || value2 == DBNull.Value))
                return true;

            // One is null/DBNull, other is not
            if (value1 == null || value1 == DBNull.Value ||
                value2 == null || value2 == DBNull.Value)
                return false;

            // Both have values - exact comparison
            return value1.Equals(value2);
        }

        /// <summary>
        /// Deletes records below MinRecId (for full table copy optimization)
        /// </summary>
        private async Task DeleteBelowMinRecIdAsync(
            string tableName,
            long minRecId,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            string deleteQuery = $"DELETE FROM [{tableName}] WHERE RecId < @MinRecId";

            using var command = new SqlCommand(deleteQuery, connection, transaction);
            command.Parameters.AddWithValue("@MinRecId", minRecId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>
        /// Deletes records by RecId list in batches
        /// </summary>
        private async Task DeleteByRecIdListAsync(
            string tableName,
            IEnumerable<long> recIds,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            var recIdList = recIds.ToList();
            if (recIdList.Count == 0)
                return;

            _logger($"[AxDB] Deleting {recIdList.Count:N0} records by RecId list");

            for (int i = 0; i < recIdList.Count; i += DELETE_BATCH_SIZE)
            {
                var batch = recIdList.Skip(i).Take(DELETE_BATCH_SIZE);
                string inClause = string.Join(",", batch);
                string deleteQuery = $"DELETE FROM [{tableName}] WHERE RecId IN ({inClause})";

                using var command = new SqlCommand(deleteQuery, connection, transaction);
                command.CommandTimeout = _connectionSettings.CommandTimeout;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Filter DataTable to only include specified RecIds
        /// </summary>
        private DataTable FilterDataTableByRecIds(DataTable source, HashSet<long> recIdsToKeep)
        {
            var filtered = source.Clone();
            string recIdCol = GetColumnName(source, "RECID") ?? "RECID";

            foreach (DataRow row in source.Rows)
            {
                if (row[recIdCol] != DBNull.Value)
                {
                    long recId = Convert.ToInt64(row[recIdCol]);
                    if (recIdsToKeep.Contains(recId))
                    {
                        filtered.ImportRow(row);
                    }
                }
            }

            return filtered;
        }

        /// <summary>
        /// Gets the minimum RecId from a DataTable
        /// </summary>
        private long GetMinRecId(DataTable data)
        {
            if (!data.Columns.Contains("RecId"))
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


        /// <summary>
        /// Gets the list of RecIds from a DataTable
        /// </summary>
        private List<long> GetRecIdList(DataTable data)
        {
            var recIds = new List<long>();

            if (!data.Columns.Contains("RecId"))
                return recIds;

            foreach (DataRow row in data.Rows)
            {
                if (row["RecId"] != DBNull.Value)
                {
                    recIds.Add(Convert.ToInt64(row["RecId"]));
                }
            }

            return recIds;
        }

        /// <summary>
        /// Gets count of changed records in AxDB (for SysRowVersion optimization)
        /// </summary>
        public async Task<long> GetChangedCountAsync(
            string tableName,
            byte[] timestampThreshold,
            CancellationToken cancellationToken)
        {
            string sql = $"SELECT COUNT(*) FROM [{tableName}] WHERE SysRowVersion > @Threshold";

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Threshold", System.Data.SqlDbType.Binary, 8).Value = timestampThreshold;
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync(cancellationToken);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        /// <summary>
        /// Gets total row count in AxDB table (for SysRowVersion optimization)
        /// </summary>
        public async Task<long> GetRowCountAsync(string tableName, CancellationToken cancellationToken)
        {
            string sql = $"SELECT COUNT(*) FROM [{tableName}]";

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync(cancellationToken);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        /// <summary>
        /// Gets all RecIds from AxDB table (for SysRowVersion optimization)
        /// </summary>
        public async Task<HashSet<long>> GetRecIdSetAsync(
            string tableName,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            string sql = $"SELECT RecId FROM [{tableName}]";

            using var command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var result = new HashSet<long>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetInt64(0));
            }

            return result;
        }

        /// <summary>
        /// Gets max SysRowVersion from AxDB table (for SysRowVersion optimization)
        /// </summary>
        public async Task<byte[]?> GetMaxTimestampAsync(
            string tableName,
            SqlConnection connection,
            SqlTransaction? transaction)
        {
            string sql = $"SELECT MAX(SysRowVersion) FROM [{tableName}]";

            using var command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var result = await command.ExecuteScalarAsync();
            return result as byte[];
        }

        /// <summary>
        /// Executes optimized incremental delete operations (for SysRowVersion optimization)
        /// </summary>
        public async Task ExecuteIncrementalDeletesAsync(
            TableInfo table,
            DataTable tier2Control,
            byte[] tier2Timestamp,
            byte[] axdbTimestamp,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            var deleteStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Create temp table with Tier2 RecIds
            _logger($"[AxDB] Creating temp table for {table.TableName}");
            await CreateTier2ControlTempTableAsync(tier2Control, connection, transaction, cancellationToken);

            // 1.1: Delete records modified in Tier2
            string delete1 = $@"
                DELETE FROM [{table.TableName}]
                WHERE RecId IN (
                    SELECT RecId FROM #Tier2Control
                    WHERE SysRowVersion > @Tier2Timestamp
                )";
            _logger($"[AxDB SQL] Delete Tier2-modified: {delete1}");

            using (var cmd = new SqlCommand(delete1, connection, transaction))
            {
                cmd.Parameters.Add("@Tier2Timestamp", System.Data.SqlDbType.Binary, 8).Value = tier2Timestamp;
                cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 1.2: Delete records modified in AxDB
            string delete2 = $@"
                DELETE FROM [{table.TableName}]
                WHERE SysRowVersion > @AxDBTimestamp";
            _logger($"[AxDB SQL] Delete AxDB-modified: {delete2}");

            using (var cmd = new SqlCommand(delete2, connection, transaction))
            {
                cmd.Parameters.Add("@AxDBTimestamp", System.Data.SqlDbType.Binary, 8).Value = axdbTimestamp;
                cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 1.3: Delete records not in Tier2 target set
            string delete3 = $@"
                DELETE FROM [{table.TableName}]
                WHERE NOT EXISTS (
                    SELECT 1 FROM #Tier2Control t
                    WHERE t.RecId = [{table.TableName}].RecId
                )";
            _logger($"[AxDB SQL] Delete not-in-Tier2: {delete3}");

            using (var cmd = new SqlCommand(delete3, connection, transaction))
            {
                cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Drop temp table
            using (var cmd = new SqlCommand("DROP TABLE #Tier2Control", connection, transaction))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            deleteStopwatch.Stop();
            table.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;
        }

        private async Task CreateTier2ControlTempTableAsync(
            DataTable tier2Control,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            // Create temp table
            string createSql = @"
                CREATE TABLE #Tier2Control (
                    RecId BIGINT PRIMARY KEY,
                    SysRowVersion BINARY(8)
                )";

            using (var cmd = new SqlCommand(createSql, connection, transaction))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Bulk insert Tier2 control data
            using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
            bulkCopy.DestinationTableName = "#Tier2Control";
            bulkCopy.ColumnMappings.Add("RecId", "RecId");
            bulkCopy.ColumnMappings.Add("SysRowVersion", "SysRowVersion");
            bulkCopy.BatchSize = 10000;
            bulkCopy.BulkCopyTimeout = _connectionSettings.CommandTimeout;

            await bulkCopy.WriteToServerAsync(tier2Control, cancellationToken);
        }
    }

    /// <summary>
    /// Context for comparison - which fields are available in BOTH databases
    /// </summary>
    public class ComparisonContext
    {
        public bool HasRecVersion { get; set; }
        public bool HasCreatedDateTime { get; set; }
        public bool HasModifiedDateTime { get; set; }

        public bool IsFallbackMode => !HasCreatedDateTime && !HasModifiedDateTime;
        public bool CanCompare => HasRecVersion;
    }

    /// <summary>
    /// Version info for a single AxDB record
    /// </summary>
    public class AxDbRecordVersion
    {
        public int RecVersion { get; set; }
        public object? CreatedDateTime { get; set; }   // DateTime, DBNull, or null
        public object? ModifiedDateTime { get; set; }  // DateTime, DBNull, or null
    }

    /// <summary>
    /// Result of comparing Tier2 data with AxDB data
    /// </summary>
    public class ComparisonResult
    {
        public HashSet<long> UnchangedRecIds { get; set; } = new HashSet<long>();
        public HashSet<long> ModifiedRecIds { get; set; } = new HashSet<long>();
        public HashSet<long> NewRecIds { get; set; } = new HashSet<long>();
        public HashSet<long> DeletedRecIds { get; set; } = new HashSet<long>();
    }
}
