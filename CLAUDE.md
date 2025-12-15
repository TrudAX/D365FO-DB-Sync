# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

D365FO Database Copy Tool is a WinForms .NET 9 application that copies data from Dynamics 365 Finance & Operations Azure SQL Database (Tier2) to local SQL Server (AxDB). It enables developers to synchronize production-like data to local development environments with sophisticated filtering and optimization strategies.

**Purpose:** Instead of performing full database restores, this tool copies only the most recently modified data, significantly reducing synchronization time.

**Target Environment:**
- Deployment: Local installation on D365FO development VMs (Windows Server 2022)
- Source Database: Azure SQL Database (Tier2) - SQL Authentication only
- Target Database: Local SQL Server 2019+ (AxDB) - SQL Authentication only

## Build and Run

```bash
# Build the solution
dotnet build DBCopyTool/DBCopyTool.sln

# Run the application
dotnet run --project DBCopyTool/DBCopyTool.csproj
```

Version format: `1.0.YYYY.DayOfYear` (auto-increments with each build using MSBuild properties)

## Architecture

### Three-Tier Service Architecture

**CopyOrchestrator** (`Services/CopyOrchestrator.cs`)
- Central coordinator managing the merged fetch+insert workflow
- Handles parallel execution with configurable worker threads
- Manages table discovery, filtering, and strategy parsing
- Orchestrates atomic table processing (fetch → insert → clear memory per table)

**Tier2DataService** (`Services/Tier2DataService.cs`)
- Handles all interactions with D365FO Azure SQL Database
- Discovers tables, fetches data, queries SQLDICTIONARY
- Implements SQLDICTIONARY caching for 10-20x speedup

**AxDbDataService** (`Services/AxDbDataService.cs`)
- Handles all interactions with local SQL Server (AxDB)
- Implements context-aware cleanup strategies
- Manages triggers, sequences, and bulk inserts via SqlBulkCopy
- Uses transactions with rollback on errors

### Data Flow (Two Stages)

1. **Discover Tables**: Discovers tables from Tier2, applies inclusion/exclusion patterns, validates schemas against AxDB, generates fetch SQL for each table
2. **Process Tables**: Parallel workers process tables atomically (each worker: fetch → insert → clear memory → next table). Default: 10 parallel workers, configurable 1-50. Memory cleared immediately after successful insert, retained only for failed tables to enable retry.

### Copy Strategy System

The tool supports two simplified strategy types defined in `Models/TableInfo.cs`:

- **RecId**: Top N records by RecId DESC (e.g., `CUSTTABLE|5000`)
- **Sql**: Custom SQL queries with placeholders (e.g., `CUSTTABLE|sql:SELECT * FROM CUSTTABLE WHERE DATAAREAID='USMF'`)

**Strategy Syntax:**
```
TableName|RecordCount|sql:CustomQuery -truncate
```

**Examples:**
- `CUSTTABLE` - RecId strategy with default count
- `SALESLINE|10000` - RecId strategy with 10,000 records
- `INVENTTRANS|sql:SELECT * FROM INVENTTRANS WHERE DATAAREAID='USMF'` - SQL strategy
- `CUSTTRANS|5000|sql:SELECT TOP (@recordCount) * FROM CUSTTRANS WHERE BLOCKED=0` - SQL with count
- `VENDTABLE|5000 -truncate` - Force truncate mode

**SQL Placeholders:**
- `*` - Replaced with actual field list (only common fields between Tier2 and AxDB)
- `@recordCount` - Replaced with record count (default or explicitly specified)
- `@sysRowVersionFilter` - Replaced with `SysRowVersion >= @Threshold AND RecId >= @MinRecId`
  - Required in SQL strategies for INCREMENTAL mode optimization
  - Without this placeholder, SQL strategies fall back to standard mode (delta comparison or TRUNCATE based on normal logic)
  - User must manually add this placeholder to their SQL query WHERE clause
  - Example: `INVENTDIM|50000|sql:SELECT * FROM INVENTDIM WHERE DATAAREAID='1000' AND @sysRowVersionFilter ORDER BY RecId DESC`

Add `-truncate` flag to any strategy to force TRUNCATE mode before insert.

### SQLDICTIONARY Caching

Critical optimization in `Models/SqlDictionaryCache.cs`:
- Loads entire SQLDICTIONARY once at startup (Tier2 and AxDB)
- Provides instant lookups for TableID and field lists
- Eliminates hundreds of individual database queries
- Cache structure: Dictionary mapping table names to TableID and fields

### Configuration Management

**ConfigManager** (`Helpers/ConfigManager.cs`)
- Stores configurations as JSON in `Config/` folder (gitignored)
- Passwords are obfuscated using `EncryptionHelper` before saving
- Tracks last used configuration in `.lastconfig` file
- Configuration names must be alphanumeric with underscores/hyphens only

**AppConfiguration** (`Models/AppConfiguration.cs`)
- Contains all settings: connections, table patterns, exclusions, strategies, parallel worker count
- Two separate exclusion lists: user-defined (`TablesToExclude`) and system-level (`SystemExcludedTables`)
- Strategy overrides use pipe-delimited format parsed in `CopyOrchestrator`
- `ParallelWorkers` setting controls concurrent table processing (default: 10)
- Timestamp storage: `Tier2Timestamps` and `AxDBTimestamps` store newline-separated entries (`TableName|0xHEXVALUE`)
- `TruncateThresholdPercent` controls INCREMENTAL vs TRUNCATE mode decision (default: 40)

**TimestampManager** (`Helpers/TimestampManager.cs`)
- Dictionary-based storage for SysRowVersion timestamps per table
- Parses hex string format: `0x0000000012345678` → byte[8]
- `LoadFromConfig()` and `SaveToConfig()` for persistence
- Auto-saved after each table completion (crash-safe)
- `TimestampsUpdated` event triggers immediate disk save in MainForm

## D365FO Specific Concepts

**SQLDICTIONARY Table**
- D365FO metadata table mapping AOT names to SQL schema
- TableID identifies each table (FieldID=0 for table definition)
- Fields identified by FIELDID <> 0, with SQLName column containing physical column name
- Essential for matching schemas between Tier2 and AxDB
- Cached at startup for 10-20x performance improvement

**RecId Field**
- Primary key in all D365FO tables (64-bit integer)
- Used for identifying and ordering records
- Sequence tables maintain next RecId value
- MinRecId from fetched data determines cleanup range

**MODIFIEDDATETIME Field**
- Standard D365FO audit field tracking last modification
- Used in delta comparison v2 for change detection
- Not all tables have this field

**SysRowVersion Field**
- Binary(8) timestamp field automatically maintained by SQL Server
- Increases monotonically with each update to the row
- Used for optimized change detection between Tier2 and AxDB
- Enables 99%+ data transfer reduction when no changes detected
- Stored as hex string format in config: `0x0000000012345678`
- Critical for INCREMENTAL vs TRUNCATE mode decision

**Sequence Management**
- After bulk insert, sequences must be updated to max(RecId)+1
- Prevents RecId conflicts on next insert
- Sequence name format: `SEQ_{TableID}` (e.g., SEQ_10878)
- Only updated if max RecId is higher than current sequence value

**D365 Table Naming Convention**
- Only tables with ALL UPPERCASE letters, numbers, and underscores are considered D365 tables
- Tables with lowercase letters (e.g., sysdiagrams, __MigrationHistory) are automatically skipped
- Pattern matching is case-insensitive but filtered by this naming rule

## Context-Aware Cleanup Logic

Located in `AxDbDataService.InsertDataAsync()` and `CopyOrchestrator.cs`. The cleanup strategy is determined by optimization mode and copy strategy.

### SysRowVersion Optimization Modes

For tables with `SysRowVersion` column, the tool uses intelligent optimization:

**First Run (Standard Mode):**
1. Fetch data from Tier2 using copy strategy (RecId or SQL)
2. Perform delta comparison v2 using RECVERSION + datetime fields
3. Smart TRUNCATE detection: Check AxDB total row count
   - If `AxDB_count - Fetched_count > Threshold%`: Auto-enable TRUNCATE mode
   - Otherwise: Use delta comparison mode
4. Save Tier2 and AxDB max(SysRowVersion) timestamps to config
5. Auto-save config to disk (crash-safe)

**Subsequent Runs (Optimized Mode):**
1. Load stored timestamps from config (Tier2 and AxDB)
2. Fetch control query: `SELECT RecId, SysRowVersion` (~1KB per 1000 records vs ~100MB for full data)
3. Calculate change percentages:
   - Tier2 changes: Records with `SysRowVersion > StoredTier2Timestamp`
   - AxDB changes: Records with `SysRowVersion > StoredAxDBTimestamp`
   - Change percent: `(Tier2Changed + AxDBChanged) / TotalRecords * 100`
4. Mode selection:
   - **INCREMENTAL Mode** (changes < threshold): 3-step selective sync
   - **TRUNCATE Mode** (changes ≥ threshold): Full table refresh

### INCREMENTAL Mode (3-Step Sync)

Used when changes < threshold (default 40%). Minimizes data transfer and processing:

**Step 1 - Delete Tier2-modified records:**
```sql
DELETE FROM AxDB.TableName
WHERE RecId IN (SELECT RecId FROM ControlData WHERE SysRowVersion > @StoredTier2Timestamp)
```

**Step 2 - Delete AxDB-modified records:**
```sql
DELETE FROM AxDB.TableName
WHERE RecId IN (SELECT RecId FROM ControlData WHERE SysRowVersion > @StoredAxDBTimestamp)
```

**Step 3 - Fetch and insert:**
1. Identify missing RecIds: Records in ControlData not in AxDB
2. Calculate fetch threshold: `MIN(MIN(SysRowVersion of missing RecIds), StoredTier2Timestamp)`
3. Fetch from Tier2: `WHERE SysRowVersion >= @Threshold AND RecId >= @MinRecId`
4. Filter client-side: Remove RecIds that already exist in AxDB
5. Bulk insert remaining records
6. Save new timestamps (Tier2 and AxDB max values)

**Why this works:**
- Deleted records in steps 1-2 will be re-inserted in step 3 (they're in ControlData but not in AxDB after delete)
- Records modified only in Tier2: Deleted in step 1, re-inserted in step 3
- Records modified only in AxDB: Deleted in step 2, re-inserted in step 3
- Records modified in both: Deleted in steps 1-2, re-inserted in step 3
- Unchanged records: Not touched (skipped)

### TRUNCATE Mode (Full Refresh)

Used when changes ≥ threshold OR `-truncate` flag OR first run with excess records:

1. `TRUNCATE TABLE AxDB.TableName`
2. Fetch all records from Tier2 using copy strategy
3. Bulk insert all records
4. Update sequence to max(RecId)+1
5. Save new timestamps (Tier2 and AxDB max values)

**Triggers TRUNCATE mode when:**
- Change percentage ≥ threshold (default 40%)
- `-truncate` flag specified in strategy
- First run detected excess records: `(AxDB_count - Fetched_count) / Fetched_count > Threshold%`

### Delta Comparison v2 (Standard Mode)

Used for tables without SysRowVersion OR when optimization not available:

1. Fetch data from Tier2 using copy strategy
2. Fetch AxDB versions: `SELECT RecId, RECVERSION, MODIFIEDDATETIME, MODIFIEDBY, CREATEDDATETIME FROM AxDB.TableName`
3. Compare using multi-field logic:
   - **Unchanged**: Same RecId + same RECVERSION (RECVERSION > 1 only)
   - **Modified**: Same RecId + different RECVERSION
   - **New in Tier2**: RecId in Tier2 but not in AxDB
   - **Deleted from AxDB**: RecId in AxDB but not in Tier2 fetched set
4. Delete strategy based on copy strategy:
   - **RecId strategy**: `DELETE WHERE RecId >= @MinRecId`
   - **SQL strategy**: `DELETE WHERE RecId >= @MinRecId`
   - **With -truncate**: `TRUNCATE TABLE`
5. Bulk insert all fetched records
6. Update sequence to max(RecId)+1

**Important Notes:**
- RECVERSION=1 records are never considered "unchanged" (always treated as new/modified)
- Delta comparison v2 uses RECVERSION as primary comparison field
- MODIFIEDDATETIME, MODIFIEDBY, CREATEDDATETIME used as tiebreakers
- When `-truncate` is used, sequence is still updated to max RecId after insert
- Timestamps auto-saved after each table (crash-safe persistence)

## Key Features

**Get SQL Feature** (right-click context menu in MainForm)
- Generates formatted SQL preview showing all operations without execution
- Displays: source query, cleanup steps (numbered), insert details, sequence update
- Shows actual TableID and sequence names
- Includes comments explaining each step and parameter placeholders
- Useful for testing and debugging strategy logic before execution
- Allows developers to copy SQL for manual testing

**Parallel Execution**
- Configurable parallel workers for merged fetch+insert workflow
- Uses `SemaphoreSlim` to limit concurrent table processing
- Default: 10 parallel workers
- Range: Min 1, Max 50 workers
- Each worker processes one table atomically: fetch → insert → clear memory → next table
- Memory-efficient: Only N tables in memory at once (where N = parallel workers)
- Connection pooling enabled with `Max Pool Size=20` in connection string

**Pattern Matching**
- Wildcard patterns support (e.g., `CUST*`, `*Staging`, `Sys*`)
- Only wildcard character is `*` (matches zero or more characters)
- Converted to regex internally: `^pattern.*$` for case-insensitive matching
- Applied to both inclusion and exclusion lists
- Exclusions applied after inclusions

**System Excluded Tables**
- Separate from user-defined exclusions
- Default exclusions: `SQL*`, `UserInfo`, `Sys*`, `Batch*`, `RetailCDX*`, `RETAILHARDWAREPROFILE`, `AIFCHANGETRACKINGDELETEDOBJECT`, `LICENSINGUSEREFFECTIVEROLES`, `TIMEZONEINFO`, `FORMRUN*`
- Managed on Connection tab with "Init" button to reset to defaults
- Combined with user exclusions during table filtering in Discover Tables stage

## Development Notes

### General
- Password handling uses simple Base64 obfuscation (not encryption) via `EncryptionHelper`
- All SQL operations are logged with timestamps `[HH:mm:ss]` to the MainForm log TextBox
- Log separator between stages: "─────────────────────────────────────────────"
- Connection strings differ for Azure SQL (with Encrypt=True, ApplicationIntent=ReadOnly) vs local SQL Server
- DataTable caching in `TableInfo.CachedData` cleared immediately after successful insert, retained only for failed tables
- Status enum progression: Pending → Fetching → Inserting → Inserted (or FetchError/InsertError)

### Configuration & Persistence
- Configuration files stored in `Config/` folder relative to application startup path (gitignored)
- Last used configuration tracked in `Config/.lastconfig` file
- Config names must be alphanumeric with underscores/hyphens only (max 100 characters)
- Alias field (max 30 chars) updates Connection tab title dynamically: "Connection-{Alias}"
- Alias used as default filename when saving new configuration
- **Timestamp persistence**: Auto-saved after EACH table completion (crash-safe)
- `TimestampsUpdated` event in CopyOrchestrator triggers immediate config save to disk
- Final config save in `ExecuteOperationAsync` finally block for UI refresh

### SysRowVersion Optimization
- **TimestampHelper** (`Helpers/TimestampHelper.cs`): Binary timestamp utility methods
  - `ToHexString(byte[])`: Converts byte[8] → "0x0000000012345678"
  - `FromHexString(string)`: Parses hex string → byte[8] (handles "0x" prefix correctly)
  - `MinTimestamp(byte[], byte[])`: Returns smaller of two timestamps
  - **Critical**: Uses `StartsWith("0x")` + `Substring(2)` to avoid removing all leading zeros
- **TimestampComparer**: IComparer for byte[] timestamp comparison
- **Smart TRUNCATE Detection**: In standard mode, checks AxDB total row count after fetch
  - Auto-enables TRUNCATE if `(AxDB_count - Fetched_count) / Fetched_count > Threshold%`
  - Ensures first run consistency with subsequent optimized runs
- **ReapplyStrategyForTable**: Reloads timestamps from config when processing single table via "Process Selected"

### Database Operations
- Insert operations use transaction with rollback on error
- Triggers always re-enabled in finally block even on error
- SqlBulkCopy settings: table lock enabled, batch size 10,000 rows
- Connection timeout only used for Tier2 (default: 3 seconds)
- Command timeout: Tier2 default 600 seconds, AxDB default 0 (unlimited)

### Memory & Performance
- Memory usage: Significantly reduced with merged workflow (only N tables in memory where N = parallel workers)
- Control queries for optimization: ~1KB per 1000 records vs ~100MB for full data
- UI updates: Table list refreshes every 3 seconds during execution, status line updates per table completion
- SQLDICTIONARY caching provides 10-20x speedup over individual metadata queries

## Common Modification Patterns

**Adding a new copy strategy:**
1. Add enum value to `CopyStrategyType` in `Models/TableInfo.cs`
2. Update `ParseStrategyLine()` in `CopyOrchestrator.cs` to parse new syntax
3. Add case to `GenerateFetchSql()` in `Tier2DataService.cs` for query generation
4. Update cleanup logic in delta comparison section of `AxDbDataService.InsertDataAsync()`
5. Update `StrategyDisplay` property in `TableInfo.cs` for UI display
6. Update help tooltip in `MainForm.Designer.cs`

**Modifying SysRowVersion optimization:**
- Threshold logic in `CopyOrchestrator.cs` within optimized mode section
- INCREMENTAL mode: 3-step delete + fetch in `CopyOrchestrator.cs`
- TRUNCATE mode: Full refresh logic in both `CopyOrchestrator.cs` and `AxDbDataService.cs`
- Control query: `FetchControlDataAsync()` in `Tier2DataService.cs`
- Timestamp comparison: `TimestampComparer` class for byte[] comparisons
- Smart TRUNCATE detection: Added after fetch in standard mode (checks AxDB total row count)

**Modifying exclusion behavior:**
- Combined exclusions handled in `CombineExclusionPatterns()` in `CopyOrchestrator.cs`
- Field exclusions support table-specific syntax: `TableName.FieldName` or global `FieldName`
- Parsed in `GetExcludedFieldsMap()` returning `Dictionary<string, List<string>>` where key "" = global
- Field matching is case-insensitive and uses exact match (no wildcards)

**Changing parallel execution:**
- Adjust `ParallelWorkers` setting in UI (Connection tab)
- SemaphoreSlim controls throttling in `ProcessTablesAsync()` and `ProcessSingleTableAsync()`
- Each worker processes one table atomically from start to finish
- Connection pool must accommodate parallel workers via connection string setting

**UI-specific modifications:**
- MainForm uses TabControl with two tabs: "Tables" (static) and "Connection-{Alias}" (dynamic)
- DataGridView bound to `List<TableInfo>` via events from CopyOrchestrator
- Context menu items: "Copy Table Name" and "Get SQL"
- Error column truncates to 50 chars, full error in tooltip
- Action buttons enabled/disabled based on execution state via event handlers
