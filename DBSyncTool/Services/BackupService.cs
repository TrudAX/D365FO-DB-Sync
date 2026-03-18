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
        /// Executes BACKUP DATABASE command against AxDB.
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

            try
            {
                var connectionString = _axDbSettings.BuildConnectionString(isAzure: false);
                using var connection = new SqlConnection(connectionString);

                connection.InfoMessage += (sender, e) =>
                {
                    _logger($"[Backup] {e.Message}");
                };

                await connection.OpenAsync(cancellationToken);

                using var command = new SqlCommand(sql, connection);
                command.CommandTimeout = 0; // Unlimited - backups can be large
                command.Parameters.AddWithValue("@path", resolvedPath);

                await command.ExecuteNonQueryAsync(cancellationToken);

                _logger($"[Backup] Backup completed successfully: {resolvedPath}");
                return (true, null);
            }
            catch (OperationCanceledException)
            {
                _logger("[Backup] Backup cancelled.");
                return (false, "Backup was cancelled.");
            }
            catch (Exception ex)
            {
                string errorMsg = $"Backup failed: {ex.Message}";
                _logger($"[Backup] ERROR: {errorMsg}");
                return (false, errorMsg);
            }
        }
    }
}
