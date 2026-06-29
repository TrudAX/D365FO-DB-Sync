namespace DBSyncTool.Models
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
        public bool ShowExcludedTables { get; set; } = false;
        public string FieldsToExclude { get; set; } = "";

        public int DefaultRecordCount { get; set; } = 10000;
        public string StrategyOverrides { get; set; } = "";

        // Parallel workers for merged fetch+insert workflow
        public int ParallelWorkers { get; set; } = 10;

        // New threshold setting for SysRowVersion optimization
        public int TruncateThresholdPercent { get; set; } = 40;

        // Force truncate mode for all tables
        public bool TruncateAllTables { get; set; } = false;

        // New timestamp storage for SysRowVersion optimization
        public string Tier2Timestamps { get; set; } = "";  // Multiline: TableName,0xTimestamp
        public string AxDBTimestamps { get; set; } = "";   // Multiline: TableName,0xTimestamp

        // MaxRecId storage for fallback mode optimization (tables without SysRowVersion)
        public string MaxTransferredRecIds { get; set; } = "";  // Multiline: TABLENAME,MaxRecId

        // System tables (not in SQLDICTIONARY) to copy with full TRUNCATE + insert.
        // Three independent lists, each with its own copy toggle. Newline-separated exact
        // table names. Honors Include/Exclude filters. (Group 1 keeps the original field
        // names for backward compatibility with existing configs.)
        public string SystemTables { get; set; } = "";
        public bool CopySystemTables { get; set; } = false;

        public string SystemTables2 { get; set; } = "";
        public bool CopySystemTables2 { get; set; } = false;

        public string SystemTables3 { get; set; } = "";
        public bool CopySystemTables3 { get; set; } = false;

        // Post-transfer SQL scripts to execute against AxDB
        public string PostTransferSqlScripts { get; set; } = "";

        // Whether to execute post-transfer scripts automatically after successful transfer
        public bool ExecutePostTransferAuto { get; set; } = false;

        // Backup Database settings
        public bool BackupDatabaseEnabled { get; set; } = false;
        public string BackupPathPattern { get; set; } = "";  // e.g. J:\MSSQL_BACKUP\AxDB_[yyyy_MM_dd_HHmm].bak

        // PowerShell script settings
        public string PowerShellScriptPath { get; set; } = "";  // Path to .ps1 file
        public bool PowerShellAutoExecute { get; set; } = false;

        // Last backup path (resolved, stored after successful backup)
        public string LastBackupPath { get; set; } = "";

        // Execute all post-transfer actions (SQL scripts, backup, any future actions)
        public bool ExecutePostTransferActions { get; set; } = false;

        // Helper method to create a default configuration
        public static AppConfiguration CreateDefault()
        {
            return new AppConfiguration();
        }
    }
}
