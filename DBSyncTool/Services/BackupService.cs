using Microsoft.Data.SqlClient;
using DBSyncTool.Models;
using System.Text.RegularExpressions;

namespace DBSyncTool.Services
{
    public class BackupService
    {
        private readonly ConnectionSettings _axDbSettings;
        private readonly Action<string> _logger;

        public BackupService(ConnectionSettings axDbSettings, Action<string> logger)
        {
            _axDbSettings = axDbSettings;
            _logger = logger;
        }

        /// <summary>
        /// Resolves date-time format tokens in the path pattern.
        /// Tokens are C# DateTime format strings enclosed in square brackets.
        /// Example: "J:\BACKUP\AxDB_[yyyy_MM_dd_HHmm].bak" -> "J:\BACKUP\AxDB_2026_03_18_1430.bak"
        /// </summary>
        public static string ResolvePathPattern(string pathPattern)
        {
            var now = DateTime.Now;
            return Regex.Replace(pathPattern, @"\[([^\]]+)\]", match =>
            {
                string format = match.Groups[1].Value;
                return now.ToString(format);
            });
        }

        /// <summary>
        /// Executes BACKUP DATABASE command against AxDB with real-time progress polling.
        /// </summary>
        public async Task<(bool Success, string? Error)> ExecuteBackupAsync(
            string pathPattern,
            string alias,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(pathPattern))
            {
                _logger("[Backup] No backup path specified.");
                return (false, "Backup path is empty.");
            }

            var (server, database) = _axDbSettings.ParseServerDatabase();
            if (string.IsNullOrWhiteSpace(database))
            {
                return (false, "Database name could not be determined from AxDB connection.");
            }

            string resolvedPath = ResolvePathPattern(pathPattern);
            string formattedDateTime = DateTime.Now.ToString("yyyy_MM_dd_HHmm");
            string safeAlias = (alias ?? "default").Replace("'", "''");
            string backupName = $"{safeAlias}_{formattedDateTime}-Full Database Backup";

            string sql = $"BACKUP DATABASE [{database}] TO DISK = @path " +
                         $"WITH COPY_ONLY, NOFORMAT, INIT, NAME = N'{backupName}', " +
                         $"SKIP, NOREWIND, NOUNLOAD, COMPRESSION, STATS = 10";

            _logger($"[Backup] Path: {resolvedPath}");
            _logger($"[Backup] Name: {backupName}");

            var connectionString = _axDbSettings.BuildConnectionString(isAzure: false);

            // CancellationTokenSource to stop progress polling when backup finishes or fails
            using var pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pollingToken = pollingCts.Token;

            // Start progress polling on a separate connection
            var pollingTask = PollBackupProgressAsync(connectionString, database, pollingToken);

            try
            {
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                connection.InfoMessage += (sender, e) =>
                {
                    foreach (var line in e.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        // Skip "X percent processed" lines - real-time polling handles progress
                        if (string.IsNullOrEmpty(trimmed) || trimmed.Contains("percent processed"))
                            continue;
                        _logger($"[Backup] {trimmed}");
                    }
                };

                using var command = new SqlCommand(sql, connection);
                command.CommandTimeout = 0; // Unlimited - backups can be large
                command.Parameters.AddWithValue("@path", resolvedPath);

                await command.ExecuteNonQueryAsync(cancellationToken);

                // Stop polling
                pollingCts.Cancel();
                await WaitForPollingToFinish(pollingTask);

                _logger($"[Backup] Backup completed successfully: {resolvedPath}");
                return (true, null);
            }
            catch (OperationCanceledException)
            {
                pollingCts.Cancel();
                await WaitForPollingToFinish(pollingTask);

                _logger("[Backup] Backup cancelled.");
                return (false, "Backup was cancelled.");
            }
            catch (Exception ex)
            {
                pollingCts.Cancel();
                await WaitForPollingToFinish(pollingTask);

                string errorMsg = $"Backup failed: {ex.Message}";
                _logger($"[Backup] ERROR: {errorMsg}");
                return (false, errorMsg);
            }
        }

        private static async Task WaitForPollingToFinish(Task pollingTask)
        {
            try { await pollingTask; }
            catch (OperationCanceledException) { }
        }

        private async Task PollBackupProgressAsync(
            string connectionString,
            string database,
            CancellationToken cancellationToken)
        {
            // Initial delay to let the backup command start
            await Task.Delay(3000, cancellationToken);

            // Use master database for DMV access; query filters by database name
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                Pooling = false  // Separate connection, not from the pool
            };
            string masterConnStr = builder.ConnectionString;

            string pollSql = @"
                SELECT r.percent_complete, r.estimated_completion_time
                FROM sys.dm_exec_requests r
                WHERE r.command LIKE 'BACKUP%'
                  AND r.database_id = DB_ID(@database)";

            try
            {
                using var connection = new SqlConnection(masterConnStr);
                await connection.OpenAsync(cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        using var command = new SqlCommand(pollSql, connection);
                        command.CommandTimeout = 5;
                        command.Parameters.AddWithValue("@database", database);

                        using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        if (await reader.ReadAsync(cancellationToken))
                        {
                            var percent = reader.GetFloat(0);
                            var estMs = reader.GetInt64(1);
                            var estTime = estMs > 0
                                ? $", Est. remaining: {TimeSpan.FromMilliseconds(estMs):mm\\:ss}"
                                : "";
                            _logger($"[Backup] Progress: {percent:F0}%{estTime}");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger($"[Backup] Progress poll error: {ex.Message}");
                    }

                    await Task.Delay(20000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal exit - backup finished or was cancelled
            }
            catch (Exception ex)
            {
                _logger($"[Backup] Progress monitor failed: {ex.Message}");
            }
        }
    }
}
