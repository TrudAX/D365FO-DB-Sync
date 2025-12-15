using System.Data;
using Microsoft.Data.SqlClient;
using DBCopyTool.Models;

namespace DBCopyTool.Services
{
    public class Tier2DataService
    {
        private readonly ConnectionSettings _connectionSettings;
        private readonly string _connectionString;
        private readonly Action<string> _logger;

        public Tier2DataService(ConnectionSettings connectionSettings, Action<string> logger)
        {
            _connectionSettings = connectionSettings;
            _connectionString = connectionSettings.BuildConnectionString(isAzure: true);
            _logger = logger;
        }

        /// <summary>
        /// Tests the connection to Tier2 database
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection.State == ConnectionState.Open;
        }

        /// <summary>
        /// Discovers all tables in Tier2 database
        /// Returns list of (TableName, RowCount, SizeGB, BytesPerRow)
        /// </summary>
        public async Task<List<(string TableName, long RowCount, decimal SizeGB, long BytesPerRow)>> DiscoverTablesAsync()
        {
            const string query = @"
                SELECT
                    o.name AS TableName,
                    MAX(s.row_count) AS [RowCount],
                    SUM(s.reserved_page_count) * 8.0 / (1024 * 1024) AS [SizeGB],
                    CASE
                        WHEN MAX(s.row_count) > 0
                        THEN (8 * 1024 * SUM(s.reserved_page_count)) / MAX(s.row_count)
                        ELSE 0
                    END AS [BytesPerRow]
                FROM sys.dm_db_partition_stats s
                INNER JOIN sys.objects o ON o.object_id = s.object_id
                WHERE o.type = 'U'
                GROUP BY o.name
                HAVING MAX(s.row_count) > 0
                ORDER BY [SizeGB] DESC";

            _logger($"[Tier2] Executing table discovery query");
            _logger($"[Tier2 SQL] {query}");

            var results = new List<(string, long, decimal, long)>();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string tableName = reader.GetString(0);
                long rowCount = reader.GetInt64(1);
                decimal sizeGB = reader.GetDecimal(2);
                long bytesPerRow = reader.GetInt64(3);

                // Only include D365 tables (uppercase letters, numbers, underscores)
                if (IsD365Table(tableName))
                {
                    results.Add((tableName, rowCount, sizeGB, bytesPerRow));
                }
            }

            return results;
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

            _logger($"[Tier2 SQL] Getting TableID for {tableName}: {query}");

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

            _logger($"[Tier2 SQL] Getting fields for TableID {tableId}");

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
        /// Fetches data from a table using RecId strategy
        /// </summary>
        public async Task<DataTable> FetchDataByRecIdAsync(string tableName, List<string> fields, int recordCount, CancellationToken cancellationToken)
        {
            string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));
            string query = $"SELECT TOP ({recordCount}) {fieldList} FROM [{tableName}] ORDER BY RecId DESC";

            _logger($"[Tier2 SQL] Fetching data from {tableName}: {query}");

            return await ExecuteQueryAsync(query, cancellationToken);
        }

        /// <summary>
        /// Fetches data from a table using ModifiedDate strategy
        /// </summary>
        public async Task<DataTable> FetchDataByModifiedDateAsync(string tableName, List<string> fields, int days, CancellationToken cancellationToken)
        {
            DateTime cutoffDate = DateTime.UtcNow.AddDays(-days);
            string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));
            string query = $"SELECT {fieldList} FROM [{tableName}] WHERE [MODIFIEDDATETIME] > @CutoffDate";

            _logger($"[Tier2 SQL] Fetching data from {tableName} (ModifiedDate > {cutoffDate:yyyy-MM-dd HH:mm:ss}): {query}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@CutoffDate", cutoffDate);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();

            await connection.OpenAsync(cancellationToken);
            using var adapter = new SqlDataAdapter(command);
            adapter.Fill(dataTable);

            return dataTable;
        }

        /// <summary>
        /// Fetches data using pre-generated SQL with optional date parameter
        /// </summary>
        public async Task<DataTable> FetchDataBySqlAsync(string tableName, string sql, CancellationToken cancellationToken)
        {
            _logger($"[Tier2 SQL] Fetching data from {tableName}: {sql}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();

            await connection.OpenAsync(cancellationToken);
            using var adapter = new SqlDataAdapter(command);
            adapter.Fill(dataTable);

            return dataTable;
        }

        /// <summary>
        /// Fetches control data (RecId, SysRowVersion) for optimized comparison
        /// </summary>
        public async Task<DataTable> FetchControlDataAsync(
            string tableName,
            int recordCount,
            string? sqlTemplate,
            CancellationToken cancellationToken)
        {
            string sql;

            if (!string.IsNullOrWhiteSpace(sqlTemplate))
            {
                // SQL strategy: Replace * with RecId, SysRowVersion
                // Also replace @recordCount placeholder if present
                sql = sqlTemplate
                    .Replace("*", "RecId, SysRowVersion", StringComparison.OrdinalIgnoreCase)
                    .Replace("@recordCount", recordCount.ToString());
            }
            else
            {
                // RecId strategy: Default control query
                sql = $@"
                SELECT TOP ({recordCount}) RecId, SysRowVersion
                FROM [{tableName}]
                ORDER BY RecId DESC";
            }

            _logger($"[Tier2 SQL] Control query: {sql}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();
            await connection.OpenAsync(cancellationToken);

            using var adapter = new SqlDataAdapter(command);
            adapter.Fill(dataTable);

            return dataTable;
        }

        /// <summary>
        /// Fetches data with SysRowVersion filter
        /// </summary>
        public async Task<DataTable> FetchDataByTimestampAsync(
            string tableName,
            List<string> fields,
            int recordCount,
            byte[] timestampThreshold,
            long minRecId,
            CancellationToken cancellationToken)
        {
            string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));
            string sql = $@"
                SELECT TOP ({recordCount}) {fieldList}
                FROM [{tableName}]
                WHERE SysRowVersion >= @Threshold
                  AND RecId >= @MinRecId
                ORDER BY RecId DESC";

            _logger($"[Tier2 SQL] Timestamp query: {sql}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Threshold", System.Data.SqlDbType.Binary, 8).Value = timestampThreshold;
            command.Parameters.AddWithValue("@MinRecId", minRecId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();
            await connection.OpenAsync(cancellationToken);

            using var adapter = new SqlDataAdapter(command);
            adapter.Fill(dataTable);

            return dataTable;
        }

        /// <summary>
        /// Executes a query and returns the result as a DataTable
        /// </summary>
        private async Task<DataTable> ExecuteQueryAsync(string query, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();

            await connection.OpenAsync(cancellationToken);
            using var adapter = new SqlDataAdapter(command);
            adapter.Fill(dataTable);

            return dataTable;
        }

        /// <summary>
        /// Loads entire SQLDICTIONARY into memory cache for fast lookups
        /// This dramatically reduces query count from ~4000 to 1 during PrepareTableList
        /// </summary>
        public async Task<SqlDictionaryCache> LoadSqlDictionaryCacheAsync()
        {
            const string query = @"
                SELECT name, TableID, FIELDID, SQLName
                FROM SQLDICTIONARY
                ORDER BY TableID, FIELDID";

            _logger("[Tier2] Loading SQLDICTIONARY cache...");
            _logger($"[Tier2 SQL] {query}");

            var cache = new SqlDictionaryCache();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

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

            _logger($"[Tier2] Loaded SQLDICTIONARY cache: {cache.GetStats()}");

            return cache;
        }

        /// <summary>
        /// Checks if a table name matches D365 naming pattern (uppercase, numbers, underscores)
        /// </summary>
        private bool IsD365Table(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return false;

            return tableName.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_');
        }
    }
}
