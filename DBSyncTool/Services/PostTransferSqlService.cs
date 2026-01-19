using Microsoft.Data.SqlClient;
using DBSyncTool.Models;

namespace DBSyncTool.Services
{
    public class PostTransferSqlService
    {
        private readonly ConnectionSettings _axDbSettings;
        private readonly Action<string> _logger;

        public PostTransferSqlService(ConnectionSettings axDbSettings, Action<string> logger)
        {
            _axDbSettings = axDbSettings;
            _logger = logger;
        }

        /// <summary>
        /// Executes SQL scripts line by line against AxDB.
        /// Skips lines starting with -- (comments) and empty lines.
        /// Stops on first error.
        /// </summary>
        /// <returns>Tuple with Success flag and Error message (null if successful)</returns>
        public async Task<(bool Success, string? Error)> ExecuteScriptsAsync(
            string scripts,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(scripts))
            {
                _logger("[Post-Transfer] No scripts to execute.");
                return (true, null);
            }

            var connectionString = _axDbSettings.BuildConnectionString(isAzure: false);
            var lines = scripts.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            int executedCount = 0;
            int skippedCount = 0;

            try
            {
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                foreach (var rawLine in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var line = rawLine.Trim();

                    // Skip empty lines (don't count as skipped)
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    // Skip comment lines (starting with --)
                    if (line.StartsWith("--"))
                    {
                        skippedCount++;
                        continue;
                    }

                    // Execute the SQL command
                    _logger($"[Post-Transfer SQL] {line}");

                    try
                    {
                        using var command = new SqlCommand(line, connection);
                        command.CommandTimeout = _axDbSettings.CommandTimeout;
                        int rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
                        _logger($"[Post-Transfer] Executed successfully. Rows affected: {rowsAffected}");
                        executedCount++;
                    }
                    catch (SqlException ex)
                    {
                        string errorMsg = $"Command failed: {line}\nError: {ex.Message}";
                        _logger($"[Post-Transfer] ERROR: {errorMsg}");
                        return (false, errorMsg);
                    }
                }

                string skippedMsg = skippedCount > 0 ? $", Skipped comments: {skippedCount}" : "";
                _logger($"[Post-Transfer] Completed. Executed: {executedCount}{skippedMsg}");
                return (true, null);
            }
            catch (OperationCanceledException)
            {
                _logger("[Post-Transfer] Execution cancelled.");
                return (false, "Execution was cancelled.");
            }
            catch (Exception ex)
            {
                string errorMsg = $"Connection or execution error: {ex.Message}";
                _logger($"[Post-Transfer] ERROR: {errorMsg}");
                return (false, errorMsg);
            }
        }
    }
}
