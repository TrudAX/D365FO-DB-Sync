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

The tool supports six strategy types defined in `Models/TableInfo.cs`:

- **RecId**: Top N records by RecId (e.g., `CUSTTABLE|5000`)
- **ModifiedDate**: Records modified in last N days (e.g., `SALESLINE|days:30`)
- **Where**: All records matching custom SQL condition (e.g., `INVENTTRANS|where:DATAAREAID='1000'`)
- **RecIdWithWhere**: RecId + WHERE combined (e.g., `CUSTTRANS|5000|where:POSTED=1`)
- **ModifiedDateWithWhere**: Days + WHERE combined (e.g., `SALESTABLE|days:14|where:STATUS=3`)
- **All**: Full table copy with automatic truncate (e.g., `VENDTABLE|all`)

Add `-truncate` flag to any strategy to force truncate before insert.

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
- Required for ModifiedDate strategies
- Not all tables have this field (validation occurs in Discover Tables stage)
- Used to calculate cutoff date: Current UTC datetime minus configured days

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

Located in `AxDbDataService.InsertDataAsync()`. The cleanup strategy is determined by the copy strategy type:

- **RecId only**: `DELETE WHERE RecId >= @MinRecId`
- **ModifiedDate only**: Two-step delete:
  1. `DELETE WHERE MODIFIEDDATETIME >= @CutoffDate`
  2. `DELETE WHERE RecId IN (@FetchedRecIdList)` (handles records modified in Tier2 with old dates in AxDB)
- **WHERE only**: Two-step delete:
  1. `DELETE WHERE (whereClause)`
  2. `DELETE WHERE RecId >= @MinRecId`
- **RecId + WHERE**: Two-step delete:
  1. `DELETE WHERE (whereClause)`
  2. `DELETE WHERE RecId >= @MinRecId`
- **ModifiedDate + WHERE**: Three-step delete:
  1. `DELETE WHERE MODIFIEDDATETIME >= @CutoffDate`
  2. `DELETE WHERE (whereClause)`
  3. `DELETE WHERE RecId IN (@FetchedRecIdList)`
- **ALL or -truncate flag**: `TRUNCATE TABLE`

**Important:** When `-truncate` is used, sequence is still updated to max RecId after insert.

This ensures clean data replacement without orphaned records. The multi-step approach handles edge cases like records that were modified in Tier2 but have old timestamps in AxDB.

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

- Password handling uses simple Base64 obfuscation (not encryption) via `EncryptionHelper`
- All SQL operations are logged with timestamps `[HH:mm:ss]` to the MainForm log TextBox
- Log separator between stages: "─────────────────────────────────────────────"
- Connection strings differ for Azure SQL (with Encrypt=True, ApplicationIntent=ReadOnly) vs local SQL Server
- DataTable caching in `TableInfo.CachedData` cleared immediately after successful insert, retained only for failed tables
- Status enum progression: Pending → Fetching → Inserting → Inserted (or FetchError/InsertError)
- Configuration files stored in `Config/` folder relative to application startup path
- Last used configuration tracked in `Config/.lastconfig` file
- Config names must be alphanumeric with underscores/hyphens only (max 100 characters)
- Alias field (max 30 chars) updates Connection tab title dynamically: "Connection-{Alias}"
- Alias used as default filename when saving new configuration
- Insert operations use transaction with rollback on error
- Triggers always re-enabled in finally block even on error
- SqlBulkCopy settings: table lock enabled, batch size 10,000 rows
- Connection timeout only used for Tier2 (default: 3 seconds)
- Command timeout: Tier2 default 600 seconds, AxDB default 0 (unlimited)
- Memory usage: Significantly reduced with merged workflow (only N tables in memory where N = parallel workers, vs all tables in old design)
- UI updates: Table list refreshes every 3 seconds during execution, status line updates per table completion

## Common Modification Patterns

**Adding a new copy strategy:**
1. Add enum value to `CopyStrategyType` in `Models/TableInfo.cs`
2. Update `ParseStrategyLine()` in `CopyOrchestrator.cs` to parse new syntax
3. Add case to `GenerateFetchSql()` for query generation
4. Update cleanup logic in `AxDbDataService.InsertDataAsync()`
5. Update `StrategyDisplay` property for UI display

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
