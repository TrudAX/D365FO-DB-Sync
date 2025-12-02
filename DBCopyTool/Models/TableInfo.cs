using System.Data;

namespace DBCopyTool.Models
{
    public class TableInfo
    {
        // Identification
        public string TableName { get; set; } = string.Empty;
        public int TableId { get; set; }

        // Strategy
        public CopyStrategyType StrategyType { get; set; }
        public int StrategyValue { get; set; }  // Kept for backward compatibility
        public int? RecIdCount { get; set; }     // For RecId strategy
        public int? DaysCount { get; set; }      // For ModifiedDate strategy
        public string WhereClause { get; set; } = string.Empty;  // Custom WHERE condition (without WHERE keyword)
        public bool UseTruncate { get; set; }    // -truncate flag

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
        public decimal InsertTimeSeconds { get; set; }
        public string Error { get; set; } = string.Empty;

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
                        parts.Add($"RecId:{RecIdCount ?? StrategyValue}");
                        break;
                    case CopyStrategyType.ModifiedDate:
                        parts.Add($"Days:{DaysCount ?? StrategyValue}");
                        break;
                    case CopyStrategyType.Where:
                        parts.Add("WHERE");
                        break;
                    case CopyStrategyType.RecIdWithWhere:
                        parts.Add($"RecId:{RecIdCount ?? StrategyValue}+WHERE");
                        break;
                    case CopyStrategyType.ModifiedDateWithWhere:
                        parts.Add($"Days:{DaysCount ?? StrategyValue}+WHERE");
                        break;
                    case CopyStrategyType.All:
                        parts.Add("ALL");
                        break;
                }

                if (UseTruncate)
                    parts.Add("TRUNC");

                return string.Join(" ", parts);
            }
        }

        public string Tier2SizeGBDisplay => Tier2SizeGB.ToString("F2");
        public string FetchTimeDisplay => FetchTimeSeconds > 0 ? FetchTimeSeconds.ToString("F2") : "";
        public string InsertTimeDisplay => InsertTimeSeconds > 0 ? InsertTimeSeconds.ToString("F2") : "";
        public string Tier2RowCountDisplay => Tier2RowCount.ToString("N0");
        public string EstimatedSizeMBDisplay => EstimatedSizeMB > 0 ? EstimatedSizeMB.ToString("F2") : "";
    }

    public enum TableStatus
    {
        Pending,
        Fetching,
        Fetched,
        FetchError,
        Inserting,
        Inserted,
        InsertError
    }

    public enum CopyStrategyType
    {
        RecId,                  // Number only
        ModifiedDate,           // days:N
        Where,                  // where:condition only
        RecIdWithWhere,         // Number + where:condition
        ModifiedDateWithWhere,  // days:N + where:condition
        All                     // Full table
    }
}
