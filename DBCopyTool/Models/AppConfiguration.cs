namespace DBCopyTool.Models
{
    public class AppConfiguration
    {
        public string ConfigName { get; set; } = "Default";
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
        public string Alias { get; set; } = "default";

        public ConnectionSettings Tier2Connection { get; set; } = new ConnectionSettings
        {
            ServerDatabase = "",
            Username = "",
            Password = "",
            ConnectionTimeout = 3,
            CommandTimeout = 600
        };

        public ConnectionSettings AxDbConnection { get; set; } = new ConnectionSettings
        {
            ServerDatabase = "localhost\\AxDB",
            Username = "",
            Password = "",
            CommandTimeout = 0
        };

        public string TablesToInclude { get; set; } = "*";
        public string TablesToExclude { get; set; } = "*Staging";
        public string SystemExcludedTables { get; set; } = "";
        public string FieldsToExclude { get; set; } = "SYSROWVERSION";

        public int DefaultRecordCount { get; set; } = 10000;
        public string StrategyOverrides { get; set; } = "";

        // Parallel workers for merged fetch+insert workflow
        public int ParallelWorkers { get; set; } = 10;

        // Helper method to create a default configuration
        public static AppConfiguration CreateDefault()
        {
            return new AppConfiguration();
        }
    }
}
