# Delta Comparison Feature - Implementation Plan

**Feature Name:** RECVERSION-based Delta Comparison  
**Date:** December 2025  
**Status:** Ready for Implementation

---

## 1. Overview

### 1.1 Purpose

Optimize AxDB insert/delete operations by comparing RECVERSION values between Tier2 and AxDB, and only processing records that have actually changed. This reduces index maintenance overhead by up to 90% when most records are unchanged.

### 1.2 Key Behavior Changes

| Aspect | Current Behavior | New Behavior |
|--------|------------------|--------------|
| Default mode | Full delete/insert | Delta comparison (if RECVERSION exists) |
| Flag | N/A | `-nocompare` to disable |
| Delete scope | All records >= MinRecId | Only modified + deleted RecIds |
| Insert scope | All fetched records | Only modified + new records |

### 1.3 Automatic Fallback

Delta comparison is **automatically skipped** when:
- RECVERSION column not present in fetched data
- `-nocompare` flag specified
- `-truncate` flag specified
- `all` strategy used (truncates anyway)

---

## 2. Data Flow

### 2.1 New Flow with Delta Comparison

```
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 1: Fetch (unchanged from current)                         │
├─────────────────────────────────────────────────────────────────┤
│ Tier2: SELECT TOP 100000 [all fields]                           │
│        FROM TABLE ORDER BY RecId DESC                           │
│        → Store in DataTable (includes RECVERSION)               │
│        → Record: MinRecId, RecordsFetched, FetchTimeSeconds     │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 2: Compare (NEW)                                          │
├─────────────────────────────────────────────────────────────────┤
│ Check: ShouldUseComparison(tableInfo)                           │
│   - RECVERSION column exists in CachedData?                     │
│   - NoCompareFlag == false?                                     │
│   - UseTruncate == false?                                       │
│   - StrategyType != All?                                        │
│                                                                  │
│ If YES:                                                          │
│   2a. AxDB Query:                                                │
│       SELECT RecId, RECVERSION FROM [TABLE]                     │
│       WHERE RecId >= @MinRecId                                  │
│       → Returns Dictionary<long, long> (RecId → RECVERSION)     │
│                                                                  │
│   2b. In-Memory Comparison:                                      │
│       For each row in Tier2 DataTable:                          │
│         - If RecId not in AxDB → NEW                            │
│         - If RecId in AxDB, same RECVERSION → UNCHANGED         │
│         - If RecId in AxDB, diff RECVERSION → MODIFIED          │
│       For each RecId in AxDB not in Tier2 → DELETED             │
│                                                                  │
│   2c. Record metrics:                                            │
│       CompareTimeSeconds, UnchangedCount, ModifiedCount,        │
│       NewInTier2Count, DeletedFromAxDbCount, ComparisonUsed     │
│                                                                  │
│ If NO:                                                           │
│   Skip to Phase 3 with current behavior                         │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 3: Delete                                                  │
├─────────────────────────────────────────────────────────────────┤
│ If comparison used:                                              │
│   DELETE WHERE RecId IN (@ModifiedRecIds + @DeletedRecIds)      │
│   → Batched in groups of 5000                                   │
│   → Only touches (Modified + Deleted) records                   │
│                                                                  │
│ If comparison NOT used:                                          │
│   Current behavior (DELETE WHERE RecId >= @MinRecId, etc.)      │
│                                                                  │
│ Record: DeleteTimeSeconds                                        │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 4: Insert                                                  │
├─────────────────────────────────────────────────────────────────┤
│ If comparison used:                                              │
│   Filter DataTable to only (Modified + New) RecIds              │
│   SqlBulkCopy filtered rows only                                │
│                                                                  │
│ If comparison NOT used:                                          │
│   SqlBulkCopy all rows (current behavior)                       │
│                                                                  │
│ Record: InsertTimeSeconds                                        │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 5: Sequence Update (unchanged)                            │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 Example Metrics

```
Input: 100,000 records fetched from Tier2
       90% unchanged, 5% modified, 4.5% new, 0.5% deleted in AxDB

Comparison Result:
  - Unchanged:        90,000 (skip)
  - Modified:          5,000 (delete + insert)
  - New in Tier2:      4,500 (insert only)
  - Deleted from AxDB:   500 (delete only)

Operations:
  - Deletes: 5,500 (vs 100,000 without comparison) - 94.5% reduction
  - Inserts: 9,500 (vs 100,000 without comparison) - 90.5% reduction
```

---

## 3. File Changes

### 3.1 Models/TableInfo.cs

**Add new properties:**

```csharp
// Strategy flags
public bool NoCompareFlag { get; set; }  // -nocompare flag to disable delta comparison

// Timing
public decimal CompareTimeSeconds { get; set; }  // Time for AxDB version query + comparison

// Delta Comparison Results
public bool ComparisonUsed { get; set; }         // Whether comparison was actually used
public int UnchangedCount { get; set; }          // Same RecId + RECVERSION
public int ModifiedCount { get; set; }           // Same RecId, different RECVERSION
public int NewInTier2Count { get; set; }         // In Tier2, not in AxDB
public int DeletedFromAxDbCount { get; set; }    // In AxDB, not in Tier2 fetched set

// Display properties
public string CompareTimeDisplay => CompareTimeSeconds > 0 ? CompareTimeSeconds.ToString("F2") : "";
public string UnchangedDisplay => ComparisonUsed ? UnchangedCount.ToString("N0") : "";
public string ModifiedDisplay => ComparisonUsed ? ModifiedCount.ToString("N0") : "";
public string NewInTier2Display => ComparisonUsed ? NewInTier2Count.ToString("N0") : "";
public string DeletedFromAxDbDisplay => ComparisonUsed ? DeletedFromAxDbCount.ToString("N0") : "";
```

**Update StrategyDisplay property:**

```csharp
public string StrategyDisplay
{
    get
    {
        var parts = new List<string>();
        // ... existing switch statement ...

        if (UseTruncate)
            parts.Add("TRUNC");

        if (NoCompareFlag)
            parts.Add("NOCMP");  // NEW: Show when comparison disabled

        return string.Join(" ", parts);
    }
}
```

---

### 3.2 Services/CopyOrchestrator.cs

**Update StrategyOverride class:**

```csharp
public class StrategyOverride
{
    public string TableName { get; set; } = string.Empty;
    public CopyStrategyType StrategyType { get; set; }
    public int? RecIdCount { get; set; }
    public int? DaysCount { get; set; }
    public string WhereClause { get; set; } = string.Empty;
    public bool UseTruncate { get; set; }
    public bool NoCompareFlag { get; set; }  // NEW
}
```

**Update ParseStrategyLine() method:**

```csharp
private StrategyOverride ParseStrategyLine(string line)
{
    // Check for -nocompare flag at the end (before -truncate check)
    bool noCompare = false;
    bool useTruncate = false;
    string workingLine = line;

    // Check for -nocompare flag
    if (workingLine.EndsWith(" -nocompare", StringComparison.OrdinalIgnoreCase))
    {
        noCompare = true;
        workingLine = workingLine.Substring(0, workingLine.Length - 11).Trim();
    }

    // Check for -truncate flag
    if (workingLine.EndsWith(" -truncate", StringComparison.OrdinalIgnoreCase))
    {
        useTruncate = true;
        workingLine = workingLine.Substring(0, workingLine.Length - 10).Trim();
    }

    // Check again for -nocompare (in case order is -truncate -nocompare)
    if (workingLine.EndsWith(" -nocompare", StringComparison.OrdinalIgnoreCase))
    {
        noCompare = true;
        workingLine = workingLine.Substring(0, workingLine.Length - 11).Trim();
    }

    // ... rest of existing parsing logic ...

    return new StrategyOverride
    {
        TableName = tableName,
        StrategyType = strategyType,
        RecIdCount = recIdCount,
        DaysCount = daysCount,
        WhereClause = whereClause,
        UseTruncate = useTruncate,
        NoCompareFlag = noCompare  // NEW
    };
}
```

**Update PrepareTableListAsync() - TableInfo creation:**

```csharp
var tableInfo = new TableInfo
{
    // ... existing properties ...
    UseTruncate = strategy.UseTruncate,
    NoCompareFlag = strategy.NoCompareFlag,  // NEW
    // ... rest of properties ...
};
```

**Update ReapplyStrategyForTable() method:**

```csharp
// Update table with new strategy
table.StrategyType = strategy.StrategyType;
// ... existing assignments ...
table.UseTruncate = strategy.UseTruncate;
table.NoCompareFlag = strategy.NoCompareFlag;  // NEW
```

**Update ProcessSingleTableAsync() - Reset comparison fields:**

```csharp
// In ProcessSingleTableAsync, after successful insert, also reset comparison fields:
table.ComparisonUsed = false;
table.UnchangedCount = 0;
table.ModifiedCount = 0;
table.NewInTier2Count = 0;
table.DeletedFromAxDbCount = 0;
table.CompareTimeSeconds = 0;
```

---

### 3.3 Services/AxDbDataService.cs

**Add new constant:**

```csharp
private const int DELETE_BATCH_SIZE = 5000;
```

**Add ComparisonResult class:**

```csharp
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
```

**Add ShouldUseComparison() method:**

```csharp
/// <summary>
/// Determines if delta comparison should be used for this table
/// </summary>
private bool ShouldUseComparison(TableInfo tableInfo)
{
    // Skip if explicitly disabled
    if (tableInfo.NoCompareFlag)
        return false;

    // Skip if truncating (no point comparing)
    if (tableInfo.UseTruncate)
        return false;

    // Skip for ALL strategy (always truncates)
    if (tableInfo.StrategyType == CopyStrategyType.All)
        return false;

    // Skip if no data
    if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
        return false;

    // Skip if RECVERSION column not present
    if (!tableInfo.CachedData.Columns.Contains("RECVERSION"))
    {
        _logger($"[AxDB] {tableInfo.TableName}: RECVERSION column not found, using full sync");
        return false;
    }

    return true;
}
```

**Add GetRecIdVersionMapAsync() method:**

```csharp
/// <summary>
/// Gets RecId -> RECVERSION mapping from AxDB for records >= minRecId
/// </summary>
private async Task<Dictionary<long, long>> GetRecIdVersionMapAsync(
    string tableName, 
    long minRecId, 
    SqlConnection connection, 
    SqlTransaction transaction,
    CancellationToken cancellationToken)
{
    var result = new Dictionary<long, long>();

    string query = $"SELECT RecId, RECVERSION FROM [{tableName}] WHERE RecId >= @MinRecId";
    _logger($"[AxDB SQL] Fetching RECVERSION map: {query} (MinRecId={minRecId})");

    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@MinRecId", minRecId);
    command.CommandTimeout = _connectionSettings.CommandTimeout;

    using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        long recId = reader.GetInt64(0);
        long recVersion = reader.GetInt64(1);
        result[recId] = recVersion;
    }

    _logger($"[AxDB] Fetched {result.Count:N0} RecId/RECVERSION pairs from AxDB");
    return result;
}
```

**Add CompareVersions() method:**

```csharp
/// <summary>
/// Compares Tier2 data with AxDB RecId/RECVERSION map
/// </summary>
private ComparisonResult CompareVersions(DataTable tier2Data, Dictionary<long, long> axDbVersions)
{
    var result = new ComparisonResult();
    var tier2RecIds = new HashSet<long>();

    foreach (DataRow row in tier2Data.Rows)
    {
        if (row["RecId"] == DBNull.Value)
            continue;

        long recId = Convert.ToInt64(row["RecId"]);
        tier2RecIds.Add(recId);

        if (!axDbVersions.TryGetValue(recId, out long axDbVersion))
        {
            // RecId not in AxDB - NEW record
            result.NewRecIds.Add(recId);
        }
        else
        {
            // RecId exists in AxDB - check RECVERSION
            long tier2Version = row["RECVERSION"] != DBNull.Value 
                ? Convert.ToInt64(row["RECVERSION"]) 
                : 0;

            if (tier2Version == axDbVersion)
            {
                result.UnchangedRecIds.Add(recId);
            }
            else
            {
                result.ModifiedRecIds.Add(recId);
            }
        }
    }

    // Find records in AxDB that are not in Tier2 fetched set (deleted)
    foreach (var axDbRecId in axDbVersions.Keys)
    {
        if (!tier2RecIds.Contains(axDbRecId))
        {
            result.DeletedRecIds.Add(axDbRecId);
        }
    }

    return result;
}
```

**Add DeleteByRecIdListAsync() method:**

```csharp
/// <summary>
/// Deletes records by RecId list in batches
/// </summary>
private async Task DeleteByRecIdListAsync(
    string tableName,
    IEnumerable<long> recIds,
    SqlConnection connection,
    SqlTransaction transaction,
    CancellationToken cancellationToken)
{
    var recIdList = recIds.ToList();
    if (recIdList.Count == 0)
        return;

    _logger($"[AxDB] Deleting {recIdList.Count:N0} records by RecId list (batch size: {DELETE_BATCH_SIZE})");

    int totalDeleted = 0;
    for (int i = 0; i < recIdList.Count; i += DELETE_BATCH_SIZE)
    {
        var batch = recIdList.Skip(i).Take(DELETE_BATCH_SIZE).ToList();
        string inClause = string.Join(",", batch);
        string deleteQuery = $"DELETE FROM [{tableName}] WHERE RecId IN ({inClause})";

        using var command = new SqlCommand(deleteQuery, connection, transaction);
        command.CommandTimeout = _connectionSettings.CommandTimeout;
        int deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        totalDeleted += deleted;
    }

    _logger($"[AxDB] Deleted {totalDeleted:N0} records");
}
```

**Add FilterDataTableByRecIds() method:**

```csharp
/// <summary>
/// Creates a new DataTable containing only rows with specified RecIds
/// </summary>
private DataTable FilterDataTableByRecIds(DataTable source, HashSet<long> recIdsToKeep)
{
    var filtered = source.Clone(); // Clone structure only

    foreach (DataRow row in source.Rows)
    {
        if (row["RecId"] != DBNull.Value)
        {
            long recId = Convert.ToInt64(row["RecId"]);
            if (recIdsToKeep.Contains(recId))
            {
                filtered.ImportRow(row);
            }
        }
    }

    return filtered;
}
```

**Update InsertDataAsync() method:**

```csharp
public async Task<int> InsertDataAsync(TableInfo tableInfo, CancellationToken cancellationToken)
{
    if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
        return 0;

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    using var transaction = connection.BeginTransaction();

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
            // PHASE: Compare
            var compareStopwatch = Stopwatch.StartNew();
            
            long minRecId = GetMinRecId(tableInfo.CachedData);
            
            // Query AxDB for RecId + RECVERSION
            var axDbVersions = await GetRecIdVersionMapAsync(
                tableInfo.TableName, minRecId, connection, transaction, cancellationToken);
            
            // Compare with Tier2 data
            comparison = CompareVersions(tableInfo.CachedData, axDbVersions);
            
            compareStopwatch.Stop();
            tableInfo.CompareTimeSeconds = (decimal)compareStopwatch.Elapsed.TotalSeconds;
            tableInfo.ComparisonUsed = true;
            tableInfo.UnchangedCount = comparison.UnchangedRecIds.Count;
            tableInfo.ModifiedCount = comparison.ModifiedRecIds.Count;
            tableInfo.NewInTier2Count = comparison.NewRecIds.Count;
            tableInfo.DeletedFromAxDbCount = comparison.DeletedRecIds.Count;
            
            int totalRecords = tableInfo.CachedData.Rows.Count;
            int changedCount = comparison.ModifiedRecIds.Count + comparison.NewRecIds.Count;
            
            _logger($"[AxDB] Compared {tableInfo.TableName}: {comparison.UnchangedRecIds.Count:N0} unchanged, " +
                   $"{comparison.ModifiedRecIds.Count:N0} modified, {comparison.NewRecIds.Count:N0} new, " +
                   $"{comparison.DeletedRecIds.Count:N0} deleted in {tableInfo.CompareTimeSeconds:F2}s");

            // If all records unchanged and nothing to delete, skip delete/insert phases
            if (comparison.ModifiedRecIds.Count == 0 && 
                comparison.NewRecIds.Count == 0 && 
                comparison.DeletedRecIds.Count == 0)
            {
                _logger($"[AxDB] {tableInfo.TableName}: All records unchanged, skipping delete/insert");
                tableInfo.DeleteTimeSeconds = 0;
                tableInfo.InsertTimeSeconds = 0;
                await transaction.CommitAsync(cancellationToken);
                return 0;
            }

            // Prepare data for delete and insert
            recIdsToDelete = new HashSet<long>(comparison.ModifiedRecIds);
            recIdsToDelete.UnionWith(comparison.DeletedRecIds);

            var recIdsToInsert = new HashSet<long>(comparison.ModifiedRecIds);
            recIdsToInsert.UnionWith(comparison.NewRecIds);

            // Filter DataTable to only rows that need inserting
            dataToInsert = FilterDataTableByRecIds(tableInfo.CachedData, recIdsToInsert);
            
            _logger($"[AxDB] {tableInfo.TableName}: Will delete {recIdsToDelete.Count:N0}, insert {dataToInsert.Rows.Count:N0} records");
        }

        // Step 1: Disable triggers (BEFORE any DELETE or INSERT operations)
        string disableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] DISABLE TRIGGER ALL";
        _logger($"[AxDB SQL] {disableTriggersSql}");
        await ExecuteNonQueryAsync(disableTriggersSql, connection, transaction);

        // Step 2: Delete existing records
        var deleteStopwatch = Stopwatch.StartNew();
        
        if (shouldCompare && comparison != null)
        {
            // Delta delete: only modified + deleted RecIds
            await DeleteByRecIdListAsync(tableInfo.TableName, recIdsToDelete, connection, transaction, cancellationToken);
        }
        else
        {
            // Full delete: current behavior based on strategy
            await DeleteExistingRecordsAsync(tableInfo, connection, transaction, cancellationToken);
        }
        
        deleteStopwatch.Stop();
        tableInfo.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;

        // Step 3: Bulk insert data
        _logger($"[AxDB] Bulk inserting {dataToInsert.Rows.Count} rows into {tableInfo.TableName}");
        var insertStopwatch = Stopwatch.StartNew();
        
        if (dataToInsert.Rows.Count > 0)
        {
            using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction))
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
        await ExecuteNonQueryAsync(enableTriggersSql, connection, transaction);

        // Step 5: Update sequence
        await UpdateSequenceAsync(tableInfo, connection, transaction, cancellationToken);

        // Commit transaction
        await transaction.CommitAsync(cancellationToken);

        return dataToInsert.Rows.Count;
    }
    catch
    {
        // Always try to re-enable triggers on error
        try
        {
            await ExecuteNonQueryAsync($"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL", connection, transaction);
        }
        catch
        {
            // Ignore errors when re-enabling triggers
        }

        await transaction.RollbackAsync(cancellationToken);
        throw;
    }
}
```

---

### 3.4 MainForm.Designer.cs

**Add new grid columns after existing columns:**

```csharp
// Add after InsertTime column, before Error column

dgvTables.Columns.Add(new DataGridViewTextBoxColumn
{
    DataPropertyName = "CompareTimeDisplay",
    HeaderText = "Compare (s)",
    Name = "CompareTime",
    Width = 80,
    DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
    SortMode = DataGridViewColumnSortMode.Automatic
});

dgvTables.Columns.Add(new DataGridViewTextBoxColumn
{
    DataPropertyName = "UnchangedDisplay",
    HeaderText = "Unchanged",
    Name = "Unchanged",
    Width = 80,
    DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
    SortMode = DataGridViewColumnSortMode.Automatic
});

dgvTables.Columns.Add(new DataGridViewTextBoxColumn
{
    DataPropertyName = "ModifiedDisplay",
    HeaderText = "Modified",
    Name = "Modified",
    Width = 70,
    DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
    SortMode = DataGridViewColumnSortMode.Automatic
});

dgvTables.Columns.Add(new DataGridViewTextBoxColumn
{
    DataPropertyName = "NewInTier2Display",
    HeaderText = "New",
    Name = "New",
    Width = 70,
    DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
    SortMode = DataGridViewColumnSortMode.Automatic
});

dgvTables.Columns.Add(new DataGridViewTextBoxColumn
{
    DataPropertyName = "DeletedFromAxDbDisplay",
    HeaderText = "Deleted",
    Name = "Deleted",
    Width = 70,
    DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
    SortMode = DataGridViewColumnSortMode.Automatic
});
```

**Update tooltip for Strategy Overrides label:**

```csharp
tooltip.SetToolTip(lblStrategyOverrides,
    "Format: TableName|SourceStrategy|where:condition -truncate -nocompare\n\n" +
    "Source strategies:\n" +
    "  5000              Top N records by RecId\n" +
    "  days:30           Records modified in last N days\n" +
    "  all               Full table copy (truncates destination)\n" +
    "  where:FIELD='X'   All records matching condition\n\n" +
    "Combinations:\n" +
    "  5000|where:DATAAREAID='1000'      Top N with filter\n" +
    "  days:30|where:DATAAREAID='1000'   Last N days with filter\n\n" +
    "Options:\n" +
    "  -truncate         Truncate destination before insert\n" +
    "  -nocompare        Disable RECVERSION comparison optimization\n\n" +
    "Examples:\n" +
    "  CUSTTABLE|5000\n" +
    "  SALESLINE|days:30\n" +
    "  INVENTTRANS|all\n" +
    "  CUSTTRANS|5000|where:DATAAREAID='1000'\n" +
    "  VENDTABLE|days:14|where:POSTED=1 -truncate\n" +
    "  BIGTABLE|5000 -nocompare");
```

---

### 3.5 MainForm.cs

**Update SortBindingList() method - add new column cases:**

```csharp
case "CompareTimeDisplay":
    sortedItems = direction == ListSortDirection.Ascending
        ? items.OrderBy(x => x.CompareTimeSeconds)
        : items.OrderByDescending(x => x.CompareTimeSeconds);
    break;
case "UnchangedDisplay":
    sortedItems = direction == ListSortDirection.Ascending
        ? items.OrderBy(x => x.UnchangedCount)
        : items.OrderByDescending(x => x.UnchangedCount);
    break;
case "ModifiedDisplay":
    sortedItems = direction == ListSortDirection.Ascending
        ? items.OrderBy(x => x.ModifiedCount)
        : items.OrderByDescending(x => x.ModifiedCount);
    break;
case "NewInTier2Display":
    sortedItems = direction == ListSortDirection.Ascending
        ? items.OrderBy(x => x.NewInTier2Count)
        : items.OrderByDescending(x => x.NewInTier2Count);
    break;
case "DeletedFromAxDbDisplay":
    sortedItems = direction == ListSortDirection.Ascending
        ? items.OrderBy(x => x.DeletedFromAxDbCount)
        : items.OrderByDescending(x => x.DeletedFromAxDbCount);
    break;
```

**Update BtnCopyToClipboard_Click() - add new columns:**

```csharp
// In the switch statement for column values:
"CompareTimeDisplay" => table.CompareTimeDisplay,
"UnchangedDisplay" => table.UnchangedDisplay,
"ModifiedDisplay" => table.ModifiedDisplay,
"NewInTier2Display" => table.NewInTier2Display,
"DeletedFromAxDbDisplay" => table.DeletedFromAxDbDisplay,
```

**Update GenerateSqlForTable() - add comparison info:**

```csharp
private string GenerateSqlForTable(TableInfo table)
{
    var sql = new System.Text.StringBuilder();

    // Header
    sql.AppendLine("-- ============================================");
    sql.AppendLine($"-- Table: {table.TableName}");
    sql.AppendLine($"-- Strategy: {table.StrategyDisplay}");
    sql.AppendLine($"-- Cleanup: {GetCleanupDescription(table)}");
    sql.AppendLine($"-- Comparison: {(table.NoCompareFlag ? "Disabled (-nocompare)" : "Enabled (if RECVERSION exists)")}");
    sql.AppendLine("-- ============================================");
    // ... rest of method
}
```

---

### 3.6 CLAUDE.md

**Add to Context-Aware Cleanup Logic section:**

```markdown
### Delta Comparison Optimization

The tool automatically uses RECVERSION-based delta comparison to minimize delete/insert operations:

**Default Behavior (comparison enabled):**
1. After fetching from Tier2, query AxDB for RecId + RECVERSION pairs (WHERE RecId >= MinRecId)
2. Compare in memory to categorize each record:
   - **Unchanged**: Same RecId and RECVERSION → Skip (no delete, no insert)
   - **Modified**: Same RecId, different RECVERSION → Delete + Insert
   - **New**: RecId in Tier2 but not in AxDB → Insert only
   - **Deleted**: RecId in AxDB but not in Tier2 set → Delete only
3. Only delete/insert the changed records

**Comparison is automatically skipped when:**
- RECVERSION column not present in table
- `-nocompare` flag specified in strategy
- `-truncate` flag specified (or `all` strategy)

**Performance Impact:**
- Typical scenario: 90% unchanged records
- Result: ~90% reduction in index maintenance operations
- New timing metric: CompareTimeSeconds tracks comparison overhead

**Strategy syntax:**
```
CUSTTABLE|5000                    # Comparison enabled (default)
SALESLINE|5000 -nocompare         # Comparison disabled
INVENTTRANS|all                   # Comparison skipped (truncate)
```
```

**Update Common Modification Patterns section:**

```markdown
**Modifying comparison behavior:**
- Default: comparison enabled if RECVERSION column exists
- To disable: add `-nocompare` flag to strategy line
- Comparison logic in `AxDbDataService.InsertDataAsync()`
- Batch size for delta deletes: DELETE_BATCH_SIZE constant (5000)
```

---

## 4. Logging Output Examples

### 4.1 With Comparison (90% unchanged)

```
[10:30:15] Starting insert for table CUSTTABLE (100000 rows)
[10:30:16] [AxDB SQL] Fetching RECVERSION map: SELECT RecId, RECVERSION FROM [CUSTTABLE] WHERE RecId >= 5000000 (MinRecId=5000000)
[10:30:18] [AxDB] Fetched 95,000 RecId/RECVERSION pairs from AxDB
[10:30:19] [AxDB] Compared CUSTTABLE: 90,000 unchanged, 5,000 modified, 4,500 new, 500 deleted in 3.45s
[10:30:19] [AxDB] CUSTTABLE: Will delete 5,500, insert 9,500 records
[10:30:19] [AxDB SQL] ALTER TABLE [CUSTTABLE] DISABLE TRIGGER ALL
[10:30:19] [AxDB] Deleting 5,500 records by RecId list (batch size: 5000)
[10:30:21] [AxDB] Deleted 5,500 records
[10:30:21] [AxDB] Bulk inserting 9500 rows into CUSTTABLE
[10:30:25] [AxDB SQL] ALTER TABLE [CUSTTABLE] ENABLE TRIGGER ALL
[10:30:25] Deleted CUSTTABLE: 2.12s, Inserted: 3.89s
[10:30:25] Completed CUSTTABLE: Total time 9.46s (Fetch: 5.23s, Compare: 3.45s, Delete: 2.12s, Insert: 3.89s)
```

### 4.2 All Records Unchanged

```
[10:35:15] Starting insert for table VENDTABLE (50000 rows)
[10:35:16] [AxDB SQL] Fetching RECVERSION map...
[10:35:17] [AxDB] Fetched 50,000 RecId/RECVERSION pairs from AxDB
[10:35:18] [AxDB] Compared VENDTABLE: 50,000 unchanged, 0 modified, 0 new, 0 deleted in 2.15s
[10:35:18] [AxDB] VENDTABLE: All records unchanged, skipping delete/insert
[10:35:18] Completed VENDTABLE: Total time 7.38s (Fetch: 5.23s, Compare: 2.15s, Delete: 0.00s, Insert: 0.00s)
```

### 4.3 Without Comparison (RECVERSION missing)

```
[10:40:15] Starting insert for table CUSTOMTABLE (10000 rows)
[10:40:15] [AxDB] CUSTOMTABLE: RECVERSION column not found, using full sync
[10:40:15] [AxDB SQL] ALTER TABLE [CUSTOMTABLE] DISABLE TRIGGER ALL
[10:40:15] [AxDB SQL] DELETE FROM [CUSTOMTABLE] WHERE RecId >= 1000
[10:40:18] [AxDB] Bulk inserting 10000 rows into CUSTOMTABLE
...
```

### 4.4 With -nocompare Flag

```
[10:45:15] Starting insert for table BIGTABLE (100000 rows)
[10:45:15] [AxDB SQL] ALTER TABLE [BIGTABLE] DISABLE TRIGGER ALL
[10:45:15] [AxDB SQL] DELETE FROM [BIGTABLE] WHERE RecId >= 5000000
...
```

---

## 5. Grid Column Layout (Updated)

| Column | Width | Property | Notes |
|--------|-------|----------|-------|
| Table Name | 150 | TableName | |
| TableID | 80 | TableId | |
| Strategy | 100 | StrategyDisplay | Shows NOCMP if -nocompare |
| Est Size (MB) | 100 | EstimatedSizeMBDisplay | |
| Tier2 Rows | 100 | Tier2RowCountDisplay | |
| Tier2 Size (GB) | 80 | Tier2SizeGBDisplay | |
| Status | 100 | Status | |
| Records Fetched | 100 | RecordsFetched | |
| Min RecId | 120 | MinRecId | |
| Fetch Time (s) | 80 | FetchTimeDisplay | |
| Delete Time (s) | 80 | DeleteTimeDisplay | |
| Insert Time (s) | 80 | InsertTimeDisplay | |
| **Compare (s)** | 80 | CompareTimeDisplay | **NEW** |
| **Unchanged** | 80 | UnchangedDisplay | **NEW** |
| **Modified** | 70 | ModifiedDisplay | **NEW** |
| **New** | 70 | NewInTier2Display | **NEW** |
| **Deleted** | 70 | DeletedFromAxDbDisplay | **NEW** |
| Error | 200 | Error | |

---

## 6. Testing Checklist

### 6.1 Functional Tests

- [ ] Default behavior: comparison enabled when RECVERSION exists
- [ ] Comparison skipped when RECVERSION column missing
- [ ] `-nocompare` flag disables comparison
- [ ] `-truncate` flag skips comparison
- [ ] `all` strategy skips comparison
- [ ] Correct categorization: unchanged, modified, new, deleted
- [ ] Correct delete count (modified + deleted only)
- [ ] Correct insert count (modified + new only)
- [ ] All records unchanged → skip delete/insert phases
- [ ] Sequence update still works after delta insert
- [ ] Triggers disabled/enabled correctly
- [ ] Transaction rollback on error

### 6.2 Performance Tests

- [ ] Compare time is measured separately
- [ ] 90% unchanged scenario shows significant speedup
- [ ] Large table (1M records) handles batch deletes correctly
- [ ] Memory usage acceptable (no duplicate DataTables)

### 6.3 UI Tests

- [ ] New columns display correctly
- [ ] Sorting works on new columns
- [ ] Copy to clipboard includes new columns
- [ ] Strategy display shows NOCMP when -nocompare used
- [ ] Get SQL shows comparison info in header

---

## 7. Risk Assessment

| Risk | Mitigation |
|------|------------|
| RECVERSION data type mismatch | Use Convert.ToInt64() for both values |
| Large AxDB result set | Dictionary<long,long> is memory efficient |
| Batch delete timeout | Use configurable command timeout |
| Comparison overhead > savings | User can disable with -nocompare |
| Transaction size with many deletes | Batching at 5000 prevents lock escalation |

---

## 8. Future Enhancements (Out of Scope)

- Configurable batch size for deletes
- Progress reporting during comparison phase
- Comparison statistics in summary log
- Option to preview changes before applying

---

*End of Implementation Plan*
