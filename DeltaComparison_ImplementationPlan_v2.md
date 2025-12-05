# Delta Comparison Feature - Implementation Plan v2

**Feature Name:** RECVERSION-based Delta Comparison with Datetime Fields  
**Date:** December 2025  
**Status:** Ready for Implementation

---

## 1. Overview

### 1.1 Purpose

Optimize AxDB insert/delete operations by comparing RECVERSION and datetime fields between Tier2 and AxDB, only processing records that have actually changed. This reduces index maintenance overhead by up to 90% when most records are unchanged.

### 1.2 Key Behavior Changes

| Aspect | Current Behavior | New Behavior |
|--------|------------------|--------------|
| Default mode | Full delete/insert | Delta comparison (if RECVERSION exists) |
| Flag | N/A | `-nocompare` to disable |
| Delete scope | All records >= MinRecId | Only modified + deleted RecIds |
| Insert scope | All fetched records | Only modified + new records |
| Sequence update | MAX(RecId) | MAX(RecId) + 10,000 gap |

### 1.3 Comparison Fields

The comparison uses the **intersection** of available fields from both Tier2 and AxDB:

| Field | Priority | Notes |
|-------|----------|-------|
| RECVERSION | Required | Must exist for comparison to be used |
| CREATEDDATETIME | Optional | Immutable, detects collisions reliably |
| MODIFIEDDATETIME | Optional | Detects collisions reliably |

### 1.4 Fallback Mode

When neither CREATEDDATETIME nor MODIFIEDDATETIME is available in the intersection:
- Records with `RECVERSION = 1` are excluded from optimization (always treated as MODIFIED)
- This prevents false matches on newly-created records that were never updated

### 1.5 Sequence Gap

After transfer, sequence is set to `MAX(RecId) + 10,000` to reduce collision probability between Tier2 and AxDB new records.

---

## 2. Comparison Logic

### 2.1 Field Intersection

```
┌─────────────────────────────────────────────────────────────────┐
│ DETERMINE COMPARISON FIELDS                                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ Step 1: Get columns from Tier2 DataTable                        │
│   tier2Columns = {RECID, RECVERSION, CREATEDDATETIME, ...}     │
│                                                                  │
│ Step 2: Get columns from AxDB (query information_schema or      │
│         fetch one row with all columns)                         │
│   axDbColumns = {RECID, RECVERSION, MODIFIEDDATETIME, ...}     │
│                                                                  │
│ Step 3: Intersection for comparison fields                      │
│   comparisonFields = tier2Columns ∩ axDbColumns ∩               │
│                      {RECVERSION, CREATEDDATETIME, MODIFIEDDATETIME}│
│                                                                  │
│ Example:                                                         │
│   Tier2 has: RECVERSION, CREATEDDATETIME, MODIFIEDDATETIME     │
│   AxDB has:  RECVERSION, MODIFIEDDATETIME                       │
│   Intersection: RECVERSION, MODIFIEDDATETIME                    │
│   (CREATEDDATETIME excluded - not in AxDB)                      │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 Comparison Context

```csharp
/// <summary>
/// Determines which fields to use for comparison (intersection of Tier2 and AxDB)
/// </summary>
public class ComparisonContext
{
    public bool HasRecVersion { get; set; }         // Must be true for comparison
    public bool HasCreatedDateTime { get; set; }    // In both Tier2 AND AxDB
    public bool HasModifiedDateTime { get; set; }   // In both Tier2 AND AxDB
    
    /// <summary>
    /// True if no datetime columns available - RECVERSION=1 must be excluded
    /// </summary>
    public bool IsFallbackMode => !HasCreatedDateTime && !HasModifiedDateTime;
    
    /// <summary>
    /// Comparison can proceed only if RECVERSION is available
    /// </summary>
    public bool CanCompare => HasRecVersion;
}
```

### 2.3 Comparison Flow

```
┌─────────────────────────────────────────────────────────────────┐
│ COMPARISON LOGIC FOR EACH TIER2 RECORD                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ Input: Tier2 record, AxDB version map, ComparisonContext        │
│                                                                  │
│ 1. RecId not in AxDB?                                           │
│    YES → NEW (insert only)                                      │
│    NO  → Continue to step 2                                     │
│                                                                  │
│ 2. Check fallback mode:                                         │
│    IF context.IsFallbackMode AND Tier2.RECVERSION = 1:         │
│       → MODIFIED (unsafe to compare, could be collision)        │
│       → Continue to next record                                 │
│                                                                  │
│ 3. Compare all available fields:                                │
│    fieldsMatch = true                                           │
│                                                                  │
│    Compare RECVERSION:                                           │
│      IF Tier2.RECVERSION ≠ AxDB.RECVERSION → fieldsMatch = false│
│                                                                  │
│    IF context.HasCreatedDateTime:                               │
│      IF Tier2.CREATEDDATETIME ≠ AxDB.CREATEDDATETIME            │
│         → fieldsMatch = false                                   │
│                                                                  │
│    IF context.HasModifiedDateTime:                              │
│      IF Tier2.MODIFIEDDATETIME ≠ AxDB.MODIFIEDDATETIME          │
│         → fieldsMatch = false                                   │
│                                                                  │
│ 4. Result:                                                       │
│    fieldsMatch = true  → UNCHANGED (skip)                       │
│    fieldsMatch = false → MODIFIED (delete + insert)             │
│                                                                  │
│ 5. AxDB records with RecId >= MinRecId not in Tier2 set:       │
│    → DELETED (delete only)                                      │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 2.4 Value Comparison Rules

```
┌─────────────────────────────────────────────────────────────────┐
│ VALUE COMPARISON                                                 │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ RECVERSION (long/bigint):                                       │
│   - Exact numeric comparison                                    │
│   - Can be negative                                             │
│   - Value of 1 means "never updated"                            │
│                                                                  │
│ CREATEDDATETIME / MODIFIEDDATETIME (datetime):                  │
│   - Exact binary comparison (no tolerance)                      │
│   - NULL = NULL is a match                                      │
│   - NULL ≠ any non-NULL value                                   │
│   - Treat as objects, use Object.Equals() for NULL handling     │
│                                                                  │
│ Examples:                                                        │
│   NULL vs NULL        → Match                                   │
│   NULL vs 2025-01-15  → No match                               │
│   2025-01-15 10:30:00.123 vs 2025-01-15 10:30:00.123 → Match   │
│   2025-01-15 10:30:00.123 vs 2025-01-15 10:30:00.127 → No match│
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 2.5 Scenarios

```
┌─────────────────────────────────────────────────────────────────┐
│ SCENARIO 1: All datetime fields available (best protection)    │
├─────────────────────────────────────────────────────────────────┤
│ Intersection: RECVERSION + CREATEDDATETIME + MODIFIEDDATETIME  │
│ Fallback mode: NO                                               │
│ RECVERSION=1 handling: Normal comparison (datetime protects)   │
│                                                                  │
│ Collision case:                                                  │
│   Tier2: RecId=1001, RECVERSION=1, CREATED=Jan-15              │
│   AxDB:  RecId=1001, RECVERSION=1, CREATED=Jan-20              │
│   Result: CREATEDDATETIME differs → MODIFIED ✓                  │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ SCENARIO 2: Only CREATEDDATETIME available                      │
├─────────────────────────────────────────────────────────────────┤
│ Intersection: RECVERSION + CREATEDDATETIME                      │
│ Fallback mode: NO                                               │
│ RECVERSION=1 handling: Normal comparison (datetime protects)   │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ SCENARIO 3: Only MODIFIEDDATETIME available                     │
├─────────────────────────────────────────────────────────────────┤
│ Intersection: RECVERSION + MODIFIEDDATETIME                     │
│ Fallback mode: NO                                               │
│ RECVERSION=1 handling: Normal comparison (datetime protects)   │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ SCENARIO 4: Only RECVERSION available (fallback mode)          │
├─────────────────────────────────────────────────────────────────┤
│ Intersection: RECVERSION only                                   │
│ Fallback mode: YES                                              │
│ RECVERSION=1 handling: Always treat as MODIFIED (unsafe)       │
│                                                                  │
│ Example:                                                         │
│   Tier2: RecId=1001, RECVERSION=1                               │
│   AxDB:  RecId=1001, RECVERSION=1                               │
│   Result: Fallback mode + RECVERSION=1 → MODIFIED (safe)       │
│                                                                  │
│   Tier2: RecId=1002, RECVERSION=54321                           │
│   AxDB:  RecId=1002, RECVERSION=54321                           │
│   Result: RECVERSION > 1, matches → UNCHANGED                   │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│ SCENARIO 5: RECVERSION not available                            │
├─────────────────────────────────────────────────────────────────┤
│ Comparison disabled - fall back to full delete/insert          │
│ Log: "RECVERSION not found, using full sync"                   │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. Sequence Gap

### 3.1 Purpose

Create a gap between transferred RecIds and new AxDB records to reduce collision probability.

### 3.2 Logic

```
┌─────────────────────────────────────────────────────────────────┐
│ SEQUENCE UPDATE WITH GAP                                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ Constant: SEQUENCE_GAP = 10000                                  │
│                                                                  │
│ After successful insert:                                         │
│   1. Get MAX(RecId) from AxDB table                             │
│   2. Get current sequence value                                  │
│   3. Calculate: NewSeq = MAX(MaxRecId, CurrentSeq) + SEQUENCE_GAP│
│   4. ALTER SEQUENCE [SEQ_xxx] RESTART WITH @NewSeq              │
│                                                                  │
│ Example:                                                         │
│   Transfer brings records up to RecId = 5,000                   │
│   Current sequence = 4,500                                       │
│   New sequence = MAX(5000, 4500) + 10000 = 15,000               │
│                                                                  │
│   Next AxDB-created record gets RecId = 15,001                  │
│   Next Tier2-created record gets RecId = 5,001                  │
│   No collision for next ~10,000 Tier2 records                   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 3.3 Combined Protection

```
┌─────────────────────────────────────────────────────────────────┐
│ THREE LAYERS OF PROTECTION                                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ Layer 1: Sequence Gap (+10,000)                                 │
│   - Prevents RecId collision for ~10k new records               │
│   - If Tier2 creates < 10k records between transfers: No collision│
│                                                                  │
│ Layer 2: CREATEDDATETIME / MODIFIEDDATETIME                     │
│   - Detects collision even if RecId matches                     │
│   - Different creation/modification time = different record     │
│                                                                  │
│ Layer 3: RECVERSION=1 Exclusion (fallback mode only)           │
│   - When no datetime fields available                           │
│   - RECVERSION=1 records always re-synced                       │
│   - Prevents false match on never-updated records               │
│                                                                  │
│ Combined: Very robust collision detection                       │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 4. Data Flow

### 4.1 Complete Flow with Comparison

```
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 1: Fetch from Tier2 (unchanged)                           │
├─────────────────────────────────────────────────────────────────┤
│ SELECT TOP X [all fields] FROM TABLE ORDER BY RecId DESC        │
│ → Store in DataTable                                            │
│ → Record: MinRecId, RecordsFetched, FetchTimeSeconds           │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 2: Determine Comparison Context                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ Check if comparison should be used:                             │
│   - NoCompareFlag = false?                                      │
│   - UseTruncate = false?                                        │
│   - StrategyType != All?                                        │
│   - RECVERSION column in Tier2 data?                            │
│                                                                  │
│ If NO to any → Skip comparison, use current full sync logic    │
│                                                                  │
│ If YES to all → Build ComparisonContext:                       │
│   1. Get Tier2 columns from DataTable                          │
│   2. Query AxDB for available columns (see 4.2)                │
│   3. Intersection for: RECVERSION, CREATEDDATETIME,            │
│      MODIFIEDDATETIME                                           │
│   4. Set HasCreatedDateTime, HasModifiedDateTime               │
│   5. Determine IsFallbackMode                                   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 3: Fetch Comparison Data from AxDB                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ Build dynamic query based on ComparisonContext:                 │
│                                                                  │
│ SELECT RecId, RECVERSION                                         │
│        [, CREATEDDATETIME]   -- if HasCreatedDateTime           │
│        [, MODIFIEDDATETIME]  -- if HasModifiedDateTime          │
│ FROM [TABLE]                                                     │
│ WHERE RecId >= @MinRecId                                        │
│                                                                  │
│ → Store in Dictionary<long, AxDbRecordVersion>                 │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 4: Compare Records                                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ For each Tier2 row:                                             │
│   - Extract RecId, RECVERSION, [CREATEDDATETIME],              │
│     [MODIFIEDDATETIME]                                          │
│   - Look up in AxDB dictionary                                  │
│   - Apply comparison logic (see section 2.3)                   │
│   - Categorize: UNCHANGED, MODIFIED, NEW                       │
│                                                                  │
│ For each AxDB RecId not in Tier2 set:                          │
│   - Categorize: DELETED                                         │
│                                                                  │
│ Record metrics:                                                  │
│   CompareTimeSeconds, UnchangedCount, ModifiedCount,           │
│   NewInTier2Count, DeletedFromAxDbCount                        │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 5: Apply Changes                                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ If all unchanged and nothing to delete:                         │
│   → Skip delete/insert, commit transaction                      │
│                                                                  │
│ Otherwise:                                                       │
│   1. Disable triggers                                           │
│   2. DELETE WHERE RecId IN (ModifiedRecIds + DeletedRecIds)    │
│      → Batched in groups of 5,000                              │
│   3. INSERT only (Modified + New) records via SqlBulkCopy      │
│   4. Enable triggers                                            │
│   5. Update sequence with +10,000 gap                          │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 4.2 Query AxDB Column Availability

```sql
-- Option A: Query INFORMATION_SCHEMA
SELECT COLUMN_NAME 
FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_NAME = @TableName 
  AND COLUMN_NAME IN ('RECVERSION', 'CREATEDDATETIME', 'MODIFIEDDATETIME')

-- Option B: Query SQLDICTIONARY (D365 specific)
SELECT SQLName 
FROM SQLDICTIONARY 
WHERE TableID = @TableId 
  AND UPPER(SQLName) IN ('RECVERSION', 'CREATEDDATETIME', 'MODIFIEDDATETIME')
```

Recommendation: Use Option A (INFORMATION_SCHEMA) as it's simpler and standard SQL Server.

---

## 5. File Changes

### 5.1 Models/TableInfo.cs

**Add new properties:**

```csharp
// Strategy flags
public bool NoCompareFlag { get; set; }  // -nocompare flag to disable delta comparison

// Timing
public decimal CompareTimeSeconds { get; set; }  // Time for comparison phase

// Delta Comparison Results
public bool ComparisonUsed { get; set; }         // Whether comparison was actually used
public int UnchangedCount { get; set; }          // All comparison fields match
public int ModifiedCount { get; set; }           // Any comparison field differs
public int NewInTier2Count { get; set; }         // RecId in Tier2, not in AxDB
public int DeletedFromAxDbCount { get; set; }    // RecId in AxDB, not in Tier2 set

// Display properties
public string CompareTimeDisplay => CompareTimeSeconds > 0 ? CompareTimeSeconds.ToString("F2") : "";
public string UnchangedDisplay => ComparisonUsed ? UnchangedCount.ToString("N0") : "";
public string ModifiedDisplay => ComparisonUsed ? ModifiedCount.ToString("N0") : "";
public string NewInTier2Display => ComparisonUsed ? NewInTier2Count.ToString("N0") : "";
public string DeletedFromAxDbDisplay => ComparisonUsed ? DeletedFromAxDbCount.ToString("N0") : "";
```

**Update StrategyDisplay:**

```csharp
if (NoCompareFlag)
    parts.Add("NOCMP");
```

---

### 5.2 Services/AxDbDataService.cs

**Add constants:**

```csharp
private const int DELETE_BATCH_SIZE = 5000;
private const int SEQUENCE_GAP = 10000;
```

**Add new classes:**

```csharp
/// <summary>
/// Context for comparison - which fields are available in BOTH databases
/// </summary>
public class ComparisonContext
{
    public bool HasRecVersion { get; set; }
    public bool HasCreatedDateTime { get; set; }
    public bool HasModifiedDateTime { get; set; }
    
    public bool IsFallbackMode => !HasCreatedDateTime && !HasModifiedDateTime;
    public bool CanCompare => HasRecVersion;
}

/// <summary>
/// Version info for a single AxDB record
/// </summary>
public class AxDbRecordVersion
{
    public long RecVersion { get; set; }
    public object? CreatedDateTime { get; set; }   // DateTime, DBNull, or null
    public object? ModifiedDateTime { get; set; }  // DateTime, DBNull, or null
}

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

**Add new methods:**

```csharp
/// <summary>
/// Determines if comparison should be used for this table
/// </summary>
private bool ShouldUseComparison(TableInfo tableInfo)
{
    if (tableInfo.NoCompareFlag)
        return false;
    if (tableInfo.UseTruncate)
        return false;
    if (tableInfo.StrategyType == CopyStrategyType.All)
        return false;
    if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
        return false;
    if (!HasColumn(tableInfo.CachedData, "RECVERSION"))
    {
        _logger($"[AxDB] {tableInfo.TableName}: RECVERSION not found, using full sync");
        return false;
    }
    return true;
}

/// <summary>
/// Check if DataTable has a column (case-insensitive)
/// </summary>
private bool HasColumn(DataTable table, string columnName)
{
    foreach (DataColumn col in table.Columns)
    {
        if (col.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

/// <summary>
/// Get column name with correct case from DataTable
/// </summary>
private string? GetColumnName(DataTable table, string columnName)
{
    foreach (DataColumn col in table.Columns)
    {
        if (col.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            return col.ColumnName;
    }
    return null;
}

/// <summary>
/// Query AxDB to determine which comparison columns exist
/// </summary>
private async Task<HashSet<string>> GetAxDbComparisonColumnsAsync(
    string tableName,
    SqlConnection connection,
    SqlTransaction transaction,
    CancellationToken cancellationToken)
{
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    
    string query = @"
        SELECT COLUMN_NAME 
        FROM INFORMATION_SCHEMA.COLUMNS 
        WHERE TABLE_NAME = @TableName 
          AND UPPER(COLUMN_NAME) IN ('RECVERSION', 'CREATEDDATETIME', 'MODIFIEDDATETIME')";
    
    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@TableName", tableName);
    command.CommandTimeout = _connectionSettings.CommandTimeout;
    
    using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        columns.Add(reader.GetString(0));
    }
    
    return columns;
}

/// <summary>
/// Build comparison context from Tier2 data and AxDB columns
/// </summary>
private ComparisonContext BuildComparisonContext(DataTable tier2Data, HashSet<string> axDbColumns)
{
    var context = new ComparisonContext();
    
    // Check intersection for each comparison field
    context.HasRecVersion = HasColumn(tier2Data, "RECVERSION") && 
                            axDbColumns.Contains("RECVERSION");
    
    context.HasCreatedDateTime = HasColumn(tier2Data, "CREATEDDATETIME") && 
                                  axDbColumns.Contains("CREATEDDATETIME");
    
    context.HasModifiedDateTime = HasColumn(tier2Data, "MODIFIEDDATETIME") && 
                                   axDbColumns.Contains("MODIFIEDDATETIME");
    
    return context;
}

/// <summary>
/// Fetch RecId and comparison fields from AxDB
/// </summary>
private async Task<Dictionary<long, AxDbRecordVersion>> GetAxDbVersionMapAsync(
    string tableName,
    long minRecId,
    ComparisonContext context,
    SqlConnection connection,
    SqlTransaction transaction,
    CancellationToken cancellationToken)
{
    var result = new Dictionary<long, AxDbRecordVersion>();
    
    // Build column list based on context
    var columns = new List<string> { "RecId", "RECVERSION" };
    if (context.HasCreatedDateTime)
        columns.Add("CREATEDDATETIME");
    if (context.HasModifiedDateTime)
        columns.Add("MODIFIEDDATETIME");
    
    string columnList = string.Join(", ", columns);
    string query = $"SELECT {columnList} FROM [{tableName}] WHERE RecId >= @MinRecId";
    
    _logger($"[AxDB SQL] Fetching version map: {query} (MinRecId={minRecId})");
    
    using var command = new SqlCommand(query, connection, transaction);
    command.Parameters.AddWithValue("@MinRecId", minRecId);
    command.CommandTimeout = _connectionSettings.CommandTimeout;
    
    using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        long recId = reader.GetInt64(0);
        var versionInfo = new AxDbRecordVersion
        {
            RecVersion = reader.GetInt64(1),
            CreatedDateTime = context.HasCreatedDateTime ? reader.GetValue(2) : null,
            ModifiedDateTime = context.HasModifiedDateTime 
                ? reader.GetValue(context.HasCreatedDateTime ? 3 : 2) 
                : null
        };
        result[recId] = versionInfo;
    }
    
    _logger($"[AxDB] Fetched {result.Count:N0} records for comparison");
    return result;
}

/// <summary>
/// Compare Tier2 data with AxDB version map
/// </summary>
private ComparisonResult CompareRecords(
    DataTable tier2Data,
    Dictionary<long, AxDbRecordVersion> axDbVersions,
    ComparisonContext context)
{
    var result = new ComparisonResult();
    var tier2RecIds = new HashSet<long>();
    
    // Get column names with correct case
    string recIdCol = GetColumnName(tier2Data, "RECID") ?? "RECID";
    string recVersionCol = GetColumnName(tier2Data, "RECVERSION") ?? "RECVERSION";
    string? createdDateTimeCol = context.HasCreatedDateTime 
        ? GetColumnName(tier2Data, "CREATEDDATETIME") : null;
    string? modifiedDateTimeCol = context.HasModifiedDateTime 
        ? GetColumnName(tier2Data, "MODIFIEDDATETIME") : null;
    
    foreach (DataRow row in tier2Data.Rows)
    {
        if (row[recIdCol] == DBNull.Value)
            continue;
        
        long recId = Convert.ToInt64(row[recIdCol]);
        tier2RecIds.Add(recId);
        
        // Check if RecId exists in AxDB
        if (!axDbVersions.TryGetValue(recId, out var axDbVersion))
        {
            result.NewRecIds.Add(recId);
            continue;
        }
        
        // Get Tier2 values
        long tier2RecVersion = Convert.ToInt64(row[recVersionCol]);
        
        // Fallback mode: exclude RECVERSION=1 from optimization
        if (context.IsFallbackMode && tier2RecVersion == 1)
        {
            result.ModifiedRecIds.Add(recId);
            continue;
        }
        
        // Compare all available fields
        bool allMatch = true;
        
        // Compare RECVERSION
        if (tier2RecVersion != axDbVersion.RecVersion)
        {
            allMatch = false;
        }
        
        // Compare CREATEDDATETIME if available
        if (allMatch && context.HasCreatedDateTime && createdDateTimeCol != null)
        {
            object tier2Value = row[createdDateTimeCol];
            object? axDbValue = axDbVersion.CreatedDateTime;
            if (!ValuesEqual(tier2Value, axDbValue))
            {
                allMatch = false;
            }
        }
        
        // Compare MODIFIEDDATETIME if available
        if (allMatch && context.HasModifiedDateTime && modifiedDateTimeCol != null)
        {
            object tier2Value = row[modifiedDateTimeCol];
            object? axDbValue = axDbVersion.ModifiedDateTime;
            if (!ValuesEqual(tier2Value, axDbValue))
            {
                allMatch = false;
            }
        }
        
        if (allMatch)
        {
            result.UnchangedRecIds.Add(recId);
        }
        else
        {
            result.ModifiedRecIds.Add(recId);
        }
    }
    
    // Find deleted records (in AxDB but not in Tier2 set)
    foreach (var axDbRecId in axDbVersions.Keys)
    {
        if (!tier2RecIds.Contains(axDbRecId))
        {
            result.DeletedRecIds.Add(axDbRecId);
        }
    }
    
    return result;
}

/// <summary>
/// Compare two values for equality (handles DBNull and null)
/// </summary>
private bool ValuesEqual(object? value1, object? value2)
{
    // Both null or DBNull
    if ((value1 == null || value1 == DBNull.Value) && 
        (value2 == null || value2 == DBNull.Value))
        return true;
    
    // One is null/DBNull, other is not
    if (value1 == null || value1 == DBNull.Value || 
        value2 == null || value2 == DBNull.Value)
        return false;
    
    // Both have values - exact comparison
    return value1.Equals(value2);
}

/// <summary>
/// Delete records by RecId list in batches
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
    
    _logger($"[AxDB] Deleting {recIdList.Count:N0} records by RecId list");
    
    for (int i = 0; i < recIdList.Count; i += DELETE_BATCH_SIZE)
    {
        var batch = recIdList.Skip(i).Take(DELETE_BATCH_SIZE);
        string inClause = string.Join(",", batch);
        string deleteQuery = $"DELETE FROM [{tableName}] WHERE RecId IN ({inClause})";
        
        using var command = new SqlCommand(deleteQuery, connection, transaction);
        command.CommandTimeout = _connectionSettings.CommandTimeout;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Filter DataTable to only include specified RecIds
/// </summary>
private DataTable FilterDataTableByRecIds(DataTable source, HashSet<long> recIdsToKeep)
{
    var filtered = source.Clone();
    string recIdCol = GetColumnName(source, "RECID") ?? "RECID";
    
    foreach (DataRow row in source.Rows)
    {
        if (row[recIdCol] != DBNull.Value)
        {
            long recId = Convert.ToInt64(row[recIdCol]);
            if (recIdsToKeep.Contains(recId))
            {
                filtered.ImportRow(row);
            }
        }
    }
    
    return filtered;
}
```

**Update UpdateSequenceAsync method:**

```csharp
private async Task UpdateSequenceAsync(
    TableInfo tableInfo, 
    SqlConnection connection, 
    SqlTransaction transaction, 
    CancellationToken cancellationToken)
{
    if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
        return;

    // Get max RecId from the table
    string maxRecIdQuery = $"SELECT MAX(RecId) FROM [{tableInfo.TableName}]";
    using var command1 = new SqlCommand(maxRecIdQuery, connection, transaction);
    command1.CommandTimeout = _connectionSettings.CommandTimeout;
    var maxRecIdResult = await command1.ExecuteScalarAsync(cancellationToken);

    if (maxRecIdResult == null || maxRecIdResult == DBNull.Value)
        return;

    long maxRecId = Convert.ToInt64(maxRecIdResult);

    // Get current sequence value
    string sequenceName = $"SEQ_{tableInfo.TableId}";
    string currentSeqQuery = "SELECT CAST(current_value AS BIGINT) FROM sys.sequences WHERE name = @SequenceName";

    using var command2 = new SqlCommand(currentSeqQuery, connection, transaction);
    command2.Parameters.AddWithValue("@SequenceName", sequenceName);
    command2.CommandTimeout = _connectionSettings.CommandTimeout;
    var currentSeqResult = await command2.ExecuteScalarAsync(cancellationToken);

    if (currentSeqResult == null || currentSeqResult == DBNull.Value)
        return;

    long currentSeq = Convert.ToInt64(currentSeqResult);

    // Calculate new sequence value with gap
    long newSeq = Math.Max(maxRecId, currentSeq) + SEQUENCE_GAP;

    string updateSeqQuery = $"ALTER SEQUENCE [{sequenceName}] RESTART WITH {newSeq}";
    _logger($"[AxDB SQL] {updateSeqQuery}");
    
    using var command3 = new SqlCommand(updateSeqQuery, connection, transaction);
    command3.CommandTimeout = _connectionSettings.CommandTimeout;
    await command3.ExecuteNonQueryAsync(cancellationToken);
}
```

**Update InsertDataAsync method:**

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

        // Determine if we should use comparison
        bool shouldCompare = ShouldUseComparison(tableInfo);
        
        ComparisonResult? comparison = null;
        DataTable dataToInsert = tableInfo.CachedData;
        HashSet<long> recIdsToDelete = new HashSet<long>();
        
        if (shouldCompare)
        {
            var compareStopwatch = Stopwatch.StartNew();
            
            // Get comparison context (intersection of Tier2 and AxDB columns)
            var axDbColumns = await GetAxDbComparisonColumnsAsync(
                tableInfo.TableName, connection, transaction, cancellationToken);
            var context = BuildComparisonContext(tableInfo.CachedData, axDbColumns);
            
            if (!context.CanCompare)
            {
                _logger($"[AxDB] {tableInfo.TableName}: RECVERSION not in both databases, using full sync");
                shouldCompare = false;
            }
            else
            {
                // Log comparison mode
                string mode = context.IsFallbackMode 
                    ? "RECVERSION only (RECVERSION=1 excluded)" 
                    : $"RECVERSION + {(context.HasCreatedDateTime ? "CREATEDDATETIME " : "")}{(context.HasModifiedDateTime ? "MODIFIEDDATETIME" : "")}".Trim();
                _logger($"[AxDB] {tableInfo.TableName}: Comparison mode: {mode}");
                
                // Fetch version map from AxDB
                long minRecId = GetMinRecId(tableInfo.CachedData);
                var axDbVersions = await GetAxDbVersionMapAsync(
                    tableInfo.TableName, minRecId, context, connection, transaction, cancellationToken);
                
                // Compare records
                comparison = CompareRecords(tableInfo.CachedData, axDbVersions, context);
                
                compareStopwatch.Stop();
                tableInfo.CompareTimeSeconds = (decimal)compareStopwatch.Elapsed.TotalSeconds;
                tableInfo.ComparisonUsed = true;
                tableInfo.UnchangedCount = comparison.UnchangedRecIds.Count;
                tableInfo.ModifiedCount = comparison.ModifiedRecIds.Count;
                tableInfo.NewInTier2Count = comparison.NewRecIds.Count;
                tableInfo.DeletedFromAxDbCount = comparison.DeletedRecIds.Count;
                
                _logger($"[AxDB] Compared {tableInfo.TableName}: {comparison.UnchangedRecIds.Count:N0} unchanged, " +
                       $"{comparison.ModifiedRecIds.Count:N0} modified, {comparison.NewRecIds.Count:N0} new, " +
                       $"{comparison.DeletedRecIds.Count:N0} deleted in {tableInfo.CompareTimeSeconds:F2}s");
                
                // Check if anything needs to be done
                if (comparison.ModifiedRecIds.Count == 0 && 
                    comparison.NewRecIds.Count == 0 && 
                    comparison.DeletedRecIds.Count == 0)
                {
                    _logger($"[AxDB] {tableInfo.TableName}: All records unchanged, skipping delete/insert");
                    tableInfo.DeleteTimeSeconds = 0;
                    tableInfo.InsertTimeSeconds = 0;
                    
                    // Still update sequence with gap
                    await UpdateSequenceAsync(tableInfo, connection, transaction, cancellationToken);
                    
                    await transaction.CommitAsync(cancellationToken);
                    return 0;
                }
                
                // Prepare for delete and insert
                recIdsToDelete = new HashSet<long>(comparison.ModifiedRecIds);
                recIdsToDelete.UnionWith(comparison.DeletedRecIds);
                
                var recIdsToInsert = new HashSet<long>(comparison.ModifiedRecIds);
                recIdsToInsert.UnionWith(comparison.NewRecIds);
                
                dataToInsert = FilterDataTableByRecIds(tableInfo.CachedData, recIdsToInsert);
                
                _logger($"[AxDB] {tableInfo.TableName}: Will delete {recIdsToDelete.Count:N0}, insert {dataToInsert.Rows.Count:N0}");
            }
        }

        // Disable triggers
        string disableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] DISABLE TRIGGER ALL";
        _logger($"[AxDB SQL] {disableTriggersSql}");
        await ExecuteNonQueryAsync(disableTriggersSql, connection, transaction);

        // Delete
        var deleteStopwatch = Stopwatch.StartNew();
        
        if (shouldCompare && comparison != null)
        {
            await DeleteByRecIdListAsync(tableInfo.TableName, recIdsToDelete, connection, transaction, cancellationToken);
        }
        else
        {
            await DeleteExistingRecordsAsync(tableInfo, connection, transaction, cancellationToken);
        }
        
        deleteStopwatch.Stop();
        tableInfo.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;

        // Insert
        var insertStopwatch = Stopwatch.StartNew();
        
        if (dataToInsert.Rows.Count > 0)
        {
            _logger($"[AxDB] Bulk inserting {dataToInsert.Rows.Count} rows into {tableInfo.TableName}");
            
            using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction);
            bulkCopy.DestinationTableName = tableInfo.TableName;
            bulkCopy.BatchSize = 10000;
            bulkCopy.BulkCopyTimeout = _connectionSettings.CommandTimeout;

            foreach (DataColumn column in dataToInsert.Columns)
            {
                bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(dataToInsert, cancellationToken);
        }
        
        insertStopwatch.Stop();
        tableInfo.InsertTimeSeconds = (decimal)insertStopwatch.Elapsed.TotalSeconds;

        // Enable triggers
        string enableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL";
        _logger($"[AxDB SQL] {enableTriggersSql}");
        await ExecuteNonQueryAsync(enableTriggersSql, connection, transaction);

        // Update sequence with gap
        await UpdateSequenceAsync(tableInfo, connection, transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return dataToInsert.Rows.Count;
    }
    catch
    {
        try
        {
            await ExecuteNonQueryAsync($"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL", connection, transaction);
        }
        catch { }

        await transaction.RollbackAsync(cancellationToken);
        throw;
    }
}
```

---

### 5.3 Services/CopyOrchestrator.cs

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

**Update ParseStrategyLine method:**

```csharp
private StrategyOverride ParseStrategyLine(string line)
{
    bool noCompare = false;
    bool useTruncate = false;
    string workingLine = line;

    // Parse flags from end (order independent)
    while (true)
    {
        if (workingLine.EndsWith(" -nocompare", StringComparison.OrdinalIgnoreCase))
        {
            noCompare = true;
            workingLine = workingLine.Substring(0, workingLine.Length - 11).Trim();
        }
        else if (workingLine.EndsWith(" -truncate", StringComparison.OrdinalIgnoreCase))
        {
            useTruncate = true;
            workingLine = workingLine.Substring(0, workingLine.Length - 10).Trim();
        }
        else
        {
            break;
        }
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

**Update TableInfo creation in PrepareTableListAsync:**

```csharp
var tableInfo = new TableInfo
{
    // ... existing properties ...
    UseTruncate = strategy.UseTruncate,
    NoCompareFlag = strategy.NoCompareFlag,  // NEW
    // ...
};
```

**Update ReapplyStrategyForTable:**

```csharp
table.NoCompareFlag = strategy.NoCompareFlag;  // NEW
```

**Update ProcessSingleTableByNameAsync - reset comparison fields:**

```csharp
// Reset comparison fields
table.ComparisonUsed = false;
table.UnchangedCount = 0;
table.ModifiedCount = 0;
table.NewInTier2Count = 0;
table.DeletedFromAxDbCount = 0;
table.CompareTimeSeconds = 0;
```

---

### 5.4 MainForm.Designer.cs

**Add new grid columns (after InsertTime, before Error):**

```csharp
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

**Update tooltip:**

```csharp
tooltip.SetToolTip(lblStrategyOverrides,
    "Format: TableName|SourceStrategy|where:condition -truncate -nocompare\n\n" +
    "Source strategies:\n" +
    "  5000              Top N records by RecId\n" +
    "  days:30           Records modified in last N days\n" +
    "  all               Full table copy (truncates destination)\n" +
    "  where:FIELD='X'   All records matching condition\n\n" +
    "Options:\n" +
    "  -truncate         Truncate destination before insert\n" +
    "  -nocompare        Disable delta comparison optimization\n\n" +
    "Examples:\n" +
    "  CUSTTABLE|5000\n" +
    "  SALESLINE|days:30 -nocompare\n" +
    "  INVENTTRANS|all");
```

---

### 5.5 MainForm.cs

**Update SortBindingList - add new cases:**

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

**Update BtnCopyToClipboard_Click - add new columns:**

```csharp
"CompareTimeDisplay" => table.CompareTimeDisplay,
"UnchangedDisplay" => table.UnchangedDisplay,
"ModifiedDisplay" => table.ModifiedDisplay,
"NewInTier2Display" => table.NewInTier2Display,
"DeletedFromAxDbDisplay" => table.DeletedFromAxDbDisplay,
```

---

## 6. Logging Examples

### 6.1 Full Comparison (datetime fields available)

```
[10:30:15] [AxDB] Starting insert for table CUSTTABLE (100000 rows)
[10:30:15] [AxDB] CUSTTABLE: Comparison mode: RECVERSION + CREATEDDATETIME MODIFIEDDATETIME
[10:30:16] [AxDB SQL] Fetching version map: SELECT RecId, RECVERSION, CREATEDDATETIME, MODIFIEDDATETIME FROM [CUSTTABLE] WHERE RecId >= 5000000 (MinRecId=5000000)
[10:30:18] [AxDB] Fetched 95,000 records for comparison
[10:30:20] [AxDB] Compared CUSTTABLE: 90,000 unchanged, 5,000 modified, 4,500 new, 500 deleted in 4.52s
[10:30:20] [AxDB] CUSTTABLE: Will delete 5,500, insert 9,500
[10:30:20] [AxDB SQL] ALTER TABLE [CUSTTABLE] DISABLE TRIGGER ALL
[10:30:20] [AxDB] Deleting 5,500 records by RecId list
[10:30:22] [AxDB] Bulk inserting 9500 rows into CUSTTABLE
[10:30:26] [AxDB SQL] ALTER TABLE [CUSTTABLE] ENABLE TRIGGER ALL
[10:30:26] [AxDB SQL] ALTER SEQUENCE [SEQ_77856] RESTART WITH 5015000
[10:30:26] Deleted CUSTTABLE: 2.12s, Inserted: 3.89s
```

### 6.2 Fallback Mode (only RECVERSION)

```
[10:35:15] [AxDB] Starting insert for table OLDTABLE (50000 rows)
[10:35:15] [AxDB] OLDTABLE: Comparison mode: RECVERSION only (RECVERSION=1 excluded)
[10:35:16] [AxDB SQL] Fetching version map: SELECT RecId, RECVERSION FROM [OLDTABLE] WHERE RecId >= 1000 (MinRecId=1000)
[10:35:17] [AxDB] Fetched 48,000 records for comparison
[10:35:18] [AxDB] Compared OLDTABLE: 42,000 unchanged, 3,000 modified, 2,000 new, 3,000 deleted in 2.34s
[10:35:18] [AxDB] OLDTABLE: Will delete 6,000, insert 5,000
...
```

### 6.3 All Records Unchanged

```
[10:40:15] [AxDB] Starting insert for table VENDTABLE (30000 rows)
[10:40:15] [AxDB] VENDTABLE: Comparison mode: RECVERSION + CREATEDDATETIME
[10:40:16] [AxDB SQL] Fetching version map...
[10:40:17] [AxDB] Fetched 30,000 records for comparison
[10:40:18] [AxDB] Compared VENDTABLE: 30,000 unchanged, 0 modified, 0 new, 0 deleted in 2.15s
[10:40:18] [AxDB] VENDTABLE: All records unchanged, skipping delete/insert
[10:40:18] [AxDB SQL] ALTER SEQUENCE [SEQ_12345] RESTART WITH 45000
```

### 6.4 Comparison Disabled (-nocompare)

```
[10:45:15] [AxDB] Starting insert for table BIGTABLE (100000 rows)
[10:45:15] [AxDB SQL] ALTER TABLE [BIGTABLE] DISABLE TRIGGER ALL
[10:45:15] [AxDB SQL] DELETE FROM [BIGTABLE] WHERE RecId >= 5000000
...
```

### 6.5 RECVERSION Not Found

```
[10:50:15] [AxDB] Starting insert for table CUSTOMTABLE (10000 rows)
[10:50:15] [AxDB] CUSTOMTABLE: RECVERSION not found, using full sync
[10:50:15] [AxDB SQL] ALTER TABLE [CUSTOMTABLE] DISABLE TRIGGER ALL
...
```

---

## 7. Testing Checklist

### 7.1 Comparison Logic Tests

- [ ] Comparison enabled by default when RECVERSION exists in both
- [ ] Comparison uses intersection of Tier2 and AxDB columns
- [ ] CREATEDDATETIME used when in both databases
- [ ] MODIFIEDDATETIME used when in both databases
- [ ] Both datetime fields used when both available
- [ ] Fallback mode when no datetime fields: RECVERSION=1 excluded
- [ ] NULL values compared correctly (NULL = NULL)
- [ ] Datetime compared exactly (no tolerance)
- [ ] Case-insensitive column name matching

### 7.2 Categorization Tests

- [ ] NEW: RecId in Tier2, not in AxDB
- [ ] UNCHANGED: All comparison fields match
- [ ] MODIFIED: Any comparison field differs
- [ ] DELETED: RecId in AxDB, not in Tier2 set
- [ ] Fallback mode: RECVERSION=1 → MODIFIED

### 7.3 Sequence Gap Tests

- [ ] Sequence updated to MAX + 10,000
- [ ] Gap applied even when all records unchanged
- [ ] Existing higher sequence values respected

### 7.4 Flag Tests

- [ ] `-nocompare` disables comparison
- [ ] `-truncate` skips comparison
- [ ] `all` strategy skips comparison
- [ ] Flags can be in any order

### 7.5 Edge Cases

- [ ] Empty Tier2 result
- [ ] All records new (empty AxDB range)
- [ ] All records deleted from Tier2
- [ ] RECVERSION column missing
- [ ] Mixed NULL and non-NULL datetime values

---

## 8. Summary

### Key Changes from v1

1. **Comparison fields**: Now uses intersection of Tier2 AND AxDB columns
2. **Multiple datetime fields**: Supports CREATEDDATETIME, MODIFIEDDATETIME, or both
3. **Fallback mode**: When no datetime fields, RECVERSION=1 records excluded
4. **Sequence gap**: +10,000 added to prevent RecId collisions
5. **Case-insensitive**: Column name matching handles any case

### Protection Layers

| Layer | Protection | When Active |
|-------|------------|-------------|
| Sequence gap | Prevents RecId collision | Always |
| CREATEDDATETIME | Detects collision by creation time | When field exists in both |
| MODIFIEDDATETIME | Detects collision by modification time | When field exists in both |
| RECVERSION=1 exclusion | Forces re-sync of never-updated records | Fallback mode only |

---

*End of Implementation Plan v2*
