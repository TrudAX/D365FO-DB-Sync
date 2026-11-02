using System.Text.Json;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// On-disk record of the nonclustered indexes that are currently disabled for a bulk load.
    /// Written before the ALTER INDEX ... DISABLE runs and deleted as soon as the indexes are
    /// back, so a file left behind means the program died mid-load.
    ///
    /// The files are a breadcrumb, not the source of truth: AxDB itself is queried
    /// (sys.indexes.is_disabled) before a run starts. They exist so the user can see which
    /// table was being loaded and when, even if the database is unreachable.
    /// </summary>
    public static class DisabledIndexStore
    {
        private const string FOLDER = "Config";
        private const string SUBFOLDER = "DisabledIndexes";
        private static readonly object _lock = new object();

        /// <summary>
        /// Name of the configuration currently loaded. Every file is prefixed with it and only
        /// matching files are listed, because each configuration points at its own AxDB - a
        /// leftover from another configuration says nothing about this database.
        /// Set by MainForm whenever a configuration is loaded or renamed.
        /// </summary>
        public static string ConfigName { get; set; } = "";

        public class Entry
        {
            public string ConfigName { get; set; } = "";
            public string TableName { get; set; } = "";
            public string Database { get; set; } = "";
            public string Server { get; set; } = "";
            public List<string> Indexes { get; set; } = new List<string>();
            public DateTime DisabledAtUtc { get; set; }
        }

        private static string FolderPath
        {
            get
            {
                string appPath;
                try
                {
                    appPath = Application.StartupPath;
                }
                catch
                {
                    appPath = Directory.GetCurrentDirectory();
                }
                return Path.Combine(appPath, FOLDER, SUBFOLDER);
            }
        }

        /// <summary>
        /// Config names are restricted to letters, digits, underscore and hyphen, and table names
        /// are D365 identifiers, so both are safe as file names - but strip anything unexpected
        /// rather than throwing from a finally block.
        /// </summary>
        private static string Sanitize(string value, string fallback)
        {
            string safe = string.Concat(value.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
            return safe.Length == 0 ? fallback : safe;
        }

        private static string FilePrefix => Sanitize(ConfigName, "NOCONFIG") + "_";

        private static string FilePath(string tableName) =>
            Path.Combine(FolderPath, $"{FilePrefix}{Sanitize(tableName, "UNKNOWN")}.json");

        /// <summary>
        /// Records that these indexes are about to be disabled. Never throws: failing to write
        /// the breadcrumb must not abort the copy.
        /// </summary>
        public static void Save(string tableName, IEnumerable<string> indexes, string server, string database)
        {
            try
            {
                var entry = new Entry
                {
                    ConfigName = ConfigName,
                    TableName = tableName,
                    Server = server,
                    Database = database,
                    Indexes = indexes.ToList(),
                    DisabledAtUtc = DateTime.UtcNow
                };

                lock (_lock)
                {
                    Directory.CreateDirectory(FolderPath);
                    File.WriteAllText(FilePath(tableName),
                        JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch
            {
                // Breadcrumb only - the pre-run scan of sys.indexes is what actually protects the data
            }
        }

        /// <summary>
        /// Drops the record for a table once its indexes are enabled again. The folder itself is
        /// left in place.
        /// </summary>
        public static void Remove(string tableName)
        {
            try
            {
                lock (_lock)
                {
                    string path = FilePath(tableName);
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
            catch
            {
                // A stale file is harmless: it only ever means "check these indexes"
            }
        }

        /// <summary>
        /// Everything still recorded as disabled FOR THE CURRENT CONFIGURATION. Files belonging
        /// to another configuration describe another AxDB and are ignored. Unreadable files are
        /// skipped.
        /// </summary>
        public static List<Entry> ListAll()
        {
            var result = new List<Entry>();
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(FolderPath))
                        return result;

                    foreach (string file in Directory.GetFiles(FolderPath, $"{FilePrefix}*.json"))
                    {
                        try
                        {
                            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                            if (entry != null && !string.IsNullOrWhiteSpace(entry.TableName))
                                result.Add(entry);
                        }
                        catch
                        {
                            // Skip a corrupt breadcrumb
                        }
                    }
                }
            }
            catch
            {
                // Fall through with whatever was read
            }
            return result;
        }

        /// <summary>
        /// Clears every record of the current configuration. Other configurations are untouched.
        /// </summary>
        public static void RemoveAll()
        {
            foreach (var entry in ListAll())
                Remove(entry.TableName);
        }
    }
}
