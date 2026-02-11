using System.Data;

namespace DBSyncTool.Models
{
    public class TableInfo
    {
        // Identification
        public string TableName { get; set; } = string.Empty;
        public int TableId { get; set; }        // Tier2 SQLDICTIONARY TableId
        public int AxDbTableId { get; set; }    // AxDB SQLDICTIONARY TableId (used for sequence updates)

        // Strategy
        public CopyStrategyType StrategyType { get; set; }
        public int? RecIdCount { get; set; }       // Explicit count or null for default
        public string SqlTemplate { get; set; } = string.Empty;  // For SQL strategy
        public bool UseTruncate { get; set; }    // -truncate flag
        public double TruncateThresholdPercent { get; set; }  // From config, used in delta comparison optimization

        // Tier2 Info
        public long Tier2RowCount { get; set; }
        public decimal Tier2SizeGB { get; set; }
        public long BytesPerRow { get; set; }
        public long RecordsToCopy { get; set; }
        public decimal EstimatedSizeMB { get; set; }
        public string FetchSql { get; set; } = string.Empty;

        // Field Info
        public List<string> CopyableFields { get; set; } = new List<string>();

        // Execution State
        public TableStatus Status { get; set; }
        public int RecordsFetched { get; set; }
        public long MinRecId { get; set; }
        public decimal FetchTimeSeconds { get; set; }
        public decimal DeleteTimeSeconds { get; set; }
        public decimal InsertTimeSeconds { get; set; }
        public decimal CompareTimeSeconds { get; set; }  // Time for AxDB version query + comparison
        public decimal TotalTimeSeconds { get; set; }    // Total wall-clock time for table processing
        public string Error { get; set; } = string.Empty;

        // Delta Comparison Results
        public bool ComparisonUsed { get; set; }         // Whether comparison was actually used
        public int UnchangedCount { get; set; }          // Same RecId + RECVERSION
        public int ModifiedCount { get; set; }           // Same RecId, different RECVERSION
        public int NewInTier2Count { get; set; }         // In Tier2, not in AxDB
        public int DeletedFromAxDbCount { get; set; }    // In AxDB, not in Tier2 fetched set

        // SysRowVersion Optimization Fields
        public bool UseOptimizedMode { get; set; }
        public byte[]? StoredTier2Timestamp { get; set; }
        public byte[]? StoredAxDBTimestamp { get; set; }
        public DataTable? ControlData { get; set; }  // RecId, SysRowVersion from Tier2

        // MaxRecId Optimization for Fallback Mode (tables without SysRowVersion)
        public long? StoredMaxRecId { get; set; }

        // Execution metrics for optimized mode
        public long Tier2ChangedCount { get; set; }
        public long AxDBChangedCount { get; set; }
        public double ChangePercent { get; set; }
        public double ExcessPercent { get; set; }
        public bool UsedTruncate { get; set; }

        // Cached Data (not persisted)
        public DataTable? CachedData { get; set; }

        // Display properties for DataGridView
        public string StrategyDisplay
        {
            get
            {
                var parts = new List<string>();

                switch (StrategyType)
                {
                    case CopyStrategyType.RecId:
                        parts.Add($"RecId:{RecIdCount ?? 0}");
                        break;
                    case CopyStrategyType.Sql:
                        parts.Add($"SQL:{RecIdCount ?? 0}");
                        break;
                }

                if (UseTruncate)
                    parts.Add("TRUNC");

                return string.Join(" ", parts);
            }
        }

        public string CoverageDisplay
        {
            get
            {
                if (Status == TableStatus.Excluded)
                    return "None";
                if (StrategyType == CopyStrategyType.Sql)
                    return "Partial";
                return RecordsToCopy >= Tier2RowCount ? "Full" : "Partial";
            }
        }

        public string Tier2SizeGBDisplay => Tier2SizeGB.ToString("F2");
        public string FetchTimeDisplay => FetchTimeSeconds.ToString("F2");
        public string DeleteTimeDisplay => DeleteTimeSeconds.ToString("F2");
        public string InsertTimeDisplay => InsertTimeSeconds.ToString("F2");
        public string CompareTimeDisplay => CompareTimeSeconds.ToString("F2");
        public string TotalTimeDisplay => TotalTimeSeconds.ToString("F2");
        public string Tier2RowCountDisplay => Tier2RowCount.ToString("N0");
        public string EstimatedSizeMBDisplay => EstimatedSizeMB > 0 ? EstimatedSizeMB.ToString("F2") : "";
        public string UnchangedDisplay => ComparisonUsed ? UnchangedCount.ToString("N0") : "";
        public string ModifiedDisplay => ComparisonUsed ? ModifiedCount.ToString("N0") : "";
        public string NewInTier2Display => ComparisonUsed ? NewInTier2Count.ToString("N0") : "";
        public string DeletedFromAxDbDisplay => ComparisonUsed ? DeletedFromAxDbCount.ToString("N0") : "";
    }

    public enum TableStatus
    {
        Pending,
        Fetching,
        Fetched,
        FetchError,
        Inserting,
        Inserted,
        InsertError,
        Excluded
    }

    public enum CopyStrategyType
    {
        RecId,  // Top N by RecId (default)
        Sql     // Custom SQL query
    }
}
