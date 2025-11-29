# D365FO Database Copy Tool - Detailed Specification

**Version:** 1.0  
**Date:** November 2025

---

## 1. Overview

### 1.1 Purpose

The D365FO Database Copy Tool is a Windows Forms application designed to synchronize data from a Tier2 Azure SQL Database to a local AxDB SQL Server database on D365FO development virtual machines. Instead of performing full database restores, this tool copies only the most recently modified data, significantly reducing synchronization time.

### 1.2 Target Environment

- **Platform:** Windows Forms (.NET)
- **Deployment:** Local installation on D365FO development VMs (Windows Server 2022)
- **Source Database:** Azure SQL Database (Tier2) - SQL Authentication only
- **Target Database:** Local SQL Server 2019+ (AxDB) - SQL Authentication only

### 1.3 Key Features

- Selective table copying with wildcard pattern support
- Advanced copy strategies:
  - Last N records by RecId
  - Records modified within N days
  - Custom WHERE clause filtering
  - Full table copy with automatic truncate
  - Combined strategies (RecId/Days + WHERE)
  - Optional truncate flag for any strategy
- System-level table exclusions (separate from user exclusions)
- Parallel execution for performance
- Stage-based execution with retry capabilities
- Configuration persistence with simple encryption
- Real-time progress monitoring
- "Get SQL" feature for testing and verification

---

## 2. Architecture

### 2.1 High-Level Components

```
┌─────────────────────────────────────────────────────────────┐
│                    Main Application Window                   │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ Connection  │  │   Tables    │  │     Execution       │  │
│  │   Config    │  │   Config    │  │      & Logs         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Data Grid (Table List)                    │
├─────────────────────────────────────────────────────────────┤
│                    Status Bar & Log Panel                    │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 Core Classes

| Class | Responsibility |
|-------|----------------|
| `MainForm` | Main UI window, orchestrates all operations |
| `ConnectionConfig` | Stores Tier2 and AxDB connection parameters |
| `TableConfig` | Stores table inclusion/exclusion patterns and strategies |
| `CopyStrategy` | Defines copy strategy (RecId count or ModifiedDate days) |
| `TableInfo` | Represents a single table's metadata and execution state |
| `Tier2DataService` | Handles all Tier2 database operations |
| `AxDbDataService` | Handles all AxDB database operations |
| `CopyOrchestrator` | Coordinates the multi-stage copy process |
| `ConfigManager` | Handles save/load of configuration files |
| `EncryptionHelper` | Provides simple obfuscation for sensitive data |

---

## 3. Configuration

### 3.1 Connection Configuration (Connection Tab)

The connection settings are displayed in a separate tab. The tab name dynamically updates based on the Alias field: "Connection-{Alias}".

#### 3.1.1 Connection Alias

| Field | Type | Description | Default |
|-------|------|-------------|---------|
| Alias | string | Short identifier for this connection (max 30 chars) | default |

- When Alias changes, the tab title updates to "Connection-{Alias}"
- Alias is used as the default filename when saving configuration
- Examples: `DM10`, `UAT`, `PROD-Copy`

#### 3.1.2 Tier2 Connection Settings

| Field | Type | Description | Default |
|-------|------|-------------|---------|
| Server\Database | string | Combined server and database in format `server\database` | (empty) |
| Username | string | SQL authentication username | (empty) |
| Password | string | SQL authentication password (obfuscated in config) | (empty) |
| Connection Timeout | int | Connection timeout in seconds | 3 |
| Command Timeout | int | Query execution timeout in seconds | 600 |

**Server\Database Format Example:**
```
spartan-srv-oce-d365ood-223535e8.database.windows.net\db_d365opsprod_ax_20251014_09440189_f2b3
```

#### 3.1.3 AxDB Connection Settings

| Field | Type | Description | Default |
|-------|------|-------------|---------|
| Server\Database | string | Combined server and database in format `server\database` | localhost\AxDB |
| Username | string | SQL authentication username | (empty) |
| Password | string | SQL authentication password (obfuscated in config) | (empty) |
| Command Timeout | int | Query execution timeout in seconds | 0 (unlimited) |

**Server\Database Format Example:**
```
localhost\AxDB
```

#### 3.1.4 Execution Settings

| Field | Type | Description | Default |
|-------|------|-------------|---------|
| Parallel Fetch Connections | int | Number of parallel connections for fetching from Tier2 | 10 |
| Parallel Insert Connections | int | Number of parallel connections for inserting to AxDB | 10 |

#### 3.1.5 System Excluded Tables

| Field | Type | Description | Default |
|-------|------|-------------|---------|
| System Excluded Tables | Multiline Text | System-level table exclusions (patterns like user exclusions) | See below |

**Default System Exclusions:**
```
SQL*
UserInfo
Sys*
Batch*
RetailCDX*
RETAILHARDWAREPROFILE
```

**Notes:**
- These exclusions are combined with user-defined "Tables to Exclude" during table filtering
- Managed separately to prevent accidental modification of system-critical exclusions
- "Init" button available to reset to defaults
- Stored in configuration files (persisted across sessions)

### 3.2 Table Selection Configuration (Tables Tab)

#### 3.2.1 Tables to Copy (Multiline Text)

Defines which tables should be copied using patterns:

- `*` - Copy all tables from Tier2
- `CustTable` - Copy only the CustTable
- `Cust*` - Copy all tables starting with "Cust"
- Multiple patterns allowed (one per line)
- Table names can be entered in any case (matching is case-insensitive)

**Note:** Pattern matching and table filtering rules are applied during the "Prepare Table List" stage. See section 5.1 for details.

#### 3.2.2 Tables to Exclude (Multiline Text)

Defines tables to exclude from copying:

- Same pattern syntax as "Tables to Copy"
- Exclusions are applied after inclusions
- Example:
  ```
  Sys*
  Bank*
  CustTable
  ```

**Default Exclusions:** (pre-populated but editable)
```
*Staging
```

**Note:** System-level exclusions (SQL*, Sys*, Batch*, etc.) are managed separately in the Connection tab under "System Excluded Tables"

#### 3.2.3 Fields to Exclude (Multiline Text)

Defines fields to exclude from copying:

**Global Exclusions** (apply to all tables):
- One field name per line
- Example: `SYSROWVERSION`

**Per-Table Exclusions** (format: `TableName.FieldName`):
- Example: `CUSTTABLE.INTERNALFIELD`

**Default Exclusions:**
```
SYSROWVERSION
```

**Matching Rules:**
- Case-insensitive
- Exact match only (no wildcards)

### 3.3 Copy Strategy Configuration

#### 3.3.1 Default Strategy

| Field | Type | Description | Default |
|-------|------|-------------|---------|
| Default Record Count | int | Number of records to copy by RecId | 10000 |

#### 3.3.2 Per-Table Strategy Overrides (Multiline Text)

**UI Note:** The label for this field should have a tooltip displaying the format and examples.

**Extended Format (Pipe-Delimited):**
```
TableName|SourceStrategy|where:condition -truncate
```

**Tooltip Text:**
```
Format: TableName|SourceStrategy|where:condition -truncate

Source strategies:
  5000              Top N records by RecId
  days:30           Records modified in last N days
  all               Full table copy (truncates destination)
  where:FIELD='X'   All records matching condition

Combinations:
  5000|where:DATAAREAID='1000'      Top N with filter
  days:30|where:DATAAREAID='1000'   Last N days with filter

Options:
  -truncate         Truncate destination before insert

Examples:
  CUSTTABLE|5000
  SALESLINE|days:30
  INVENTTRANS|all
  CUSTTRANS|5000|where:DATAAREAID='1000'
  VENDTABLE|days:14|where:POSTED=1 -truncate
```

**Format Rules:**

| Pattern | Meaning |
|---------|---------|
| `CUSTTABLE` | Use default strategy (RecId with default count) |
| `CUSTTABLE\|5000` | Top 5000 by RecId |
| `CUSTTABLE\|days:30` | Last 30 days by ModifiedDateTime |
| `CUSTTABLE\|all` | Full table (implies truncate) |
| `CUSTTABLE\|5000 -truncate` | Top 5000, truncate destination first |
| `CUSTTABLE\|where:DATAAREAID='1000'` | All records matching WHERE |
| `CUSTTABLE\|5000\|where:DATAAREAID='1000'` | Top 5000 matching WHERE |
| `CUSTTABLE\|days:30\|where:DATAAREAID='1000'` | Last 30 days matching WHERE |
| `CUSTTABLE\|where:DATAAREAID='1000' -truncate` | All matching WHERE, truncate first |
| `CUSTTABLE\|5000\|where:DATAAREAID='1000' -truncate` | Top 5000 matching WHERE, truncate first |

**Parsing Rules:**
- Case-insensitive table name matching
- WHERE clause passed directly to SQL (no validation)
- `-truncate` can be added to any strategy (except `all` which implies it)
- Invalid format → Error with line number on "Prepare Table List" stage

**Cleanup Rules by Strategy:**

| Source Strategy | Default Cleanup | With -truncate |
|-----------------|-----------------|----------------|
| `5000` (RecId only) | DELETE WHERE RecId >= @MinRecId | TRUNCATE |
| `days:30` (DateTime only) | DELETE WHERE ModifiedDateTime >= @MinDate; DELETE WHERE RecId IN (@FetchedIds) | TRUNCATE |
| `where:...` (WHERE only) | DELETE WHERE {same condition}; DELETE WHERE RecId >= @MinRecId | TRUNCATE |
| `5000\|where:...` (RecId + WHERE) | DELETE WHERE {same condition}; DELETE WHERE RecId >= @MinRecId | TRUNCATE |
| `days:30\|where:...` (DateTime + WHERE) | DELETE WHERE ModifiedDateTime >= @MinDate; DELETE WHERE {same condition}; DELETE WHERE RecId IN (@FetchedIds) | TRUNCATE |
| `all` (Full table) | TRUNCATE | TRUNCATE |

**Important:** When `-truncate` is used, sequence is still updated to max RecId after insert.

#### 3.3.3 Strategy Validation

During "Prepare Table List" stage:
- If a table is configured for ModifiedDate strategy but lacks `MODIFIEDDATETIME` column → Error status for that table
- Strategy override parsing errors → Stop execution and display error

### 3.4 Configuration File Management

#### 3.4.1 Storage Location

- All configs stored in: `<ApplicationPath>\Config\`
- File format: `<ConfigName>.json`
- Config names: Alphanumeric, underscore, hyphen only; max 100 characters

#### 3.4.2 Auto-Load Behavior

- On startup, load the last used configuration
- Store last used config name in: `<ApplicationPath>\Config\.lastconfig`
- If no configs exist, auto-create "Default" configuration

#### 3.4.3 Save/Load Operations

- **Save:** Saves current settings to the currently loaded config file
- **Save As:** Prompts for new config name (text input, not file dialog); defaults to current Alias value
- **Load:** Shows list of available configs in `Config\` folder

#### 3.4.4 Configuration File Structure

```json
{
  "configName": "DM10",
  "lastModified": "2025-11-28T10:30:00Z",
  "alias": "DM10",
  "tier2Connection": {
    "serverDatabase": "spartan-srv-oce-d365opsprod-2c47723535e8.database.windows.net\\db_d365opsprod_blundstonepp_ax_20251014_09440189_f2b3",
    "username": "admin",
    "password": "<obfuscated>",
    "connectionTimeout": 3,
    "commandTimeout": 600
  },
  "axDbConnection": {
    "serverDatabase": "localhost\\AxDB",
    "username": "sa",
    "password": "<obfuscated>",
    "commandTimeout": 0
  },
  "tablesToCopy": "*",
  "tablesToExclude": "Sys*\nBatch*",
  "fieldsToExclude": "SYSROWVERSION",
  "defaultRecordCount": 10000,
  "strategyOverrides": "CUSTTABLE:5000\nSALESLINE:days:30",
  "parallelFetchConnections": 10,
  "parallelInsertConnections": 10
}
```

#### 3.4.5 Password Obfuscation

- Use Base64 encoding for password obfuscation
- This is for obfuscation only, not security
- Encode on save, decode on load

---

## 4. Database Operations

### 4.1 Table Discovery (Tier2)

#### 4.1.1 Get All Tables Query

```sql
SELECT 
    o.name AS TableName,
    MAX(s.row_count) AS RowCount,
    SUM(s.reserved_page_count) * 8.0 / (1024 * 1024) AS SizeGB,
    CASE 
        WHEN MAX(s.row_count) > 0 
        THEN (8 * 1024 * SUM(s.reserved_page_count)) / MAX(s.row_count) 
        ELSE 0 
    END AS BytesPerRow
FROM sys.dm_db_partition_stats s
INNER JOIN sys.objects o ON o.object_id = s.object_id
WHERE o.type = 'U'
GROUP BY o.name
HAVING MAX(s.row_count) > 0
ORDER BY SizeGB DESC
```

#### 4.1.2 Table Name Filtering

Only include tables where name matches pattern: `^[A-Z0-9_]+$` (all uppercase letters, numbers, underscores)

### 4.2 Metadata Queries

#### 4.2.1 Get TableID

Execute on both Tier2 and AxDB:

```sql
SELECT TableID 
FROM SQLDICTIONARY 
WHERE UPPER(name) = UPPER(@TableName) 
  AND FIELDID = 0
```

- If no result in either database → Table cannot be copied (log to UI)

#### 4.2.2 Get Table Fields

Execute on both Tier2 and AxDB:

```sql
SELECT SQLName 
FROM SQLDICTIONARY 
WHERE TableID = @TableId 
  AND FIELDID <> 0
```

#### 4.2.3 Determine Copyable Fields

1. Get Tier2 fields (T2Fields)
2. Get AxDB fields (AxFields)
3. Get excluded fields for this table (global + per-table)
4. Copyable fields = T2Fields ∩ AxFields - ExcludedFields

### 4.3 Data Fetch Queries (Tier2)

#### 4.3.1 RecId Strategy

```sql
SELECT TOP (@RecordCount) [Field1], [Field2], ... 
FROM [@TableName] 
ORDER BY RecId DESC
```

#### 4.3.2 ModifiedDate Strategy

```sql
SELECT [Field1], [Field2], ... 
FROM [@TableName] 
WHERE [MODIFIEDDATETIME] > @CutoffDateTime
```

Where `@CutoffDateTime` = Current UTC datetime minus configured days

### 4.4 Data Insert Operations (AxDB)

#### 4.4.1 Pre-Insert: Delete Existing Records

**For RecId Strategy:**
```sql
DELETE FROM [@TableName] 
WHERE RecId >= @MinRecIdFromFetchedData
```

**For ModifiedDate Strategy:**
```sql
-- Delete by date
DELETE FROM [@TableName] 
WHERE [MODIFIEDDATETIME] >= @MinModifiedDateFromFetchedData

-- Delete by RecId (handles records modified in Tier2 that had old dates in AxDB)
DELETE FROM [@TableName] 
WHERE RecId IN (@FetchedRecIdList)
```

#### 4.4.2 Insert Data

1. Disable all triggers on table:
   ```sql
   ALTER TABLE [@TableName] DISABLE TRIGGER ALL
   ```

2. Use SqlBulkCopy with:
   - Table lock for performance
   - Column mappings based on copyable fields
   - Batch size: 10000 rows (or configurable)

3. Re-enable triggers (always, even on error):
   ```sql
   ALTER TABLE [@TableName] ENABLE TRIGGER ALL
   ```

#### 4.4.3 Update Sequence

```sql
-- Get current max RecId in table
DECLARE @MaxRecId BIGINT
SELECT @MaxRecId = MAX(RecId) FROM [@TableName]

-- Get current sequence value
DECLARE @CurrentSeq BIGINT
SELECT @CurrentSeq = CAST(current_value AS BIGINT) 
FROM sys.sequences 
WHERE name = 'SEQ_@TableId'

-- Only update if max RecId is higher
IF @MaxRecId > @CurrentSeq
BEGIN
    ALTER SEQUENCE [SEQ_@TableId] RESTART WITH @MaxRecId
END
```

### 4.5 Connection Management

#### 4.5.1 Connection Pooling

- Use SqlConnection with connection pooling enabled
- Pool size should accommodate parallel workers
- Connection string should include: `Pooling=true; Max Pool Size=20`

#### 4.5.2 Error Handling

- On connection failure during parallel operations → Signal all workers to stop immediately
- Workers should check cancellation token between operations
- Log connection errors with full exception details

---

## 5. Execution Stages

### 5.1 Stage 1: Prepare Table List

#### 5.1.1 Pattern Matching Rules

During this stage, the following rules are applied to filter tables:

- **Case-insensitive comparison:** Table names from Tier2 are compared to patterns case-insensitively
- **Wildcard support:** `*` is the only wildcard character (matches zero or more characters)
- **D365 Table Filter:** Only tables with ALL UPPERCASE letters, numbers, and underscores in the name are considered (e.g., `CUSTTABLE`, `TABLE123`, `CUST_TABLE_BR`)
- **Automatic exclusion:** Tables with lowercase letters (e.g., `sysdiagrams`, `__MigrationHistory`) are automatically skipped

#### 5.1.2 Process Flow

```
1. Clear existing table list and cache
2. Validate strategy override syntax (stop on error)
3. Connect to Tier2
4. Execute table discovery query
5. For each discovered table:
   a. Check if name matches D365 table pattern (all uppercase + numbers + underscores only; skip if not)
   b. Apply inclusion patterns (case-insensitive)
   c. Apply exclusion patterns (case-insensitive)
   d. Get TableID from Tier2 SQLDICTIONARY (skip if not found, log)
   e. Get TableID from AxDB SQLDICTIONARY (skip if not found, log)
   f. Determine copy strategy (default or override)
   g. If ModifiedDate strategy, verify MODIFIEDDATETIME column exists
   h. Get field lists from both databases
   i. Calculate copyable fields
   j. Generate fetch SQL
   k. Add to table list with status "Pending"
6. Update status: "Prepared X tables, Y skipped"
```

#### 5.1.2 Output Columns

| Column | Description |
|--------|-------------|
| Table Name | Name of the table |
| TableID | ID from SQLDICTIONARY |
| Strategy | "RecId: 10000" or "Days: 30" |
| Tier2 Rows | Total row count in Tier2 |
| Tier2 Size (GB) | Total size in Tier2 |
| Fetch SQL | Generated SQL query (for debugging) |
| Status | Pending |

### 5.2 Stage 2: Get Data

#### 5.2.1 Process Flow

```
1. Validate table list exists (error if not)
2. Create cancellation token source
3. Create parallel task pool (configured count)
4. For each table with status "Pending":
   a. Set status to "Fetching"
   b. Execute fetch SQL on Tier2
   c. Store result in memory cache (DataTable)
   d. Record: Records fetched, Min RecId, Fetch time
   e. Set status to "Fetched" or "FetchError"
5. On any connection error: Cancel all workers
6. Update status: "Fetched X tables, Y failed"
```

#### 5.2.2 Updated Columns

| Column | Description |
|--------|-------------|
| Records Fetched | Number of records retrieved |
| Min RecId | Minimum RecId in fetched data |
| Fetch Time (s) | Time to execute fetch |
| Status | Fetched / FetchError |
| Error | Error message if failed |

### 5.3 Stage 3: Insert Data

#### 5.3.1 Process Flow

```
1. Validate fetched data exists (error if not)
2. Create cancellation token source
3. Create parallel task pool (configured count)
4. For each table with status "Fetched":
   a. Set status to "Inserting"
   b. Begin transaction
   c. Execute delete statements
   d. Disable triggers
   e. Execute SqlBulkCopy
   f. Enable triggers (in finally block)
   g. Update sequence if needed
   h. Commit transaction
   i. Record: Insert time
   j. Set status to "Inserted" or "InsertError"
5. On any connection error: Cancel all workers
6. Update status: "Inserted X tables, Y failed"
```

#### 5.3.2 Updated Columns

| Column | Description |
|--------|-------------|
| Insert Time (s) | Time to execute insert |
| Status | Inserted / InsertError |
| Error | Error message if failed |

### 5.4 Stage 4: Insert Failed (Retry)

#### 5.4.1 Process Flow

```
1. Filter tables with status "InsertError"
2. For each table:
   a. Verify cached data still exists
   b. Execute same insert process as Stage 3
```

#### 5.4.2 Availability

- Button enabled only when:
  - At least one table has status "InsertError"
  - Cache has not been cleared (no "Prepare Table List" since "Get Data")

### 5.5 Run All Stages

#### 5.5.1 Process Flow

```
1. Execute Stage 1: Prepare Table List
2. If successful, execute Stage 2: Get Data
3. If successful, execute Stage 3: Insert Data
4. Update overall status after each stage
```

#### 5.5.2 Stop Behavior

- "Stop" button cancels immediately
- Current operations are interrupted
- Tables in progress may be left incomplete
- Status updates to show current state

---

## 6. User Interface

### 6.1 Main Window Layout

```
┌─────────────────────────────────────────────────────────────────────────┐
│  D365FO Database Copy Tool                                   [_][□][X] │
├─────────────────────────────────────────────────────────────────────────┤
│ Config: [Default        ▼] [Save] [Save As] [Load]                      │
├─────────────────────────────────────────────────────────────────────────┤
│ ┌─Tables─┐ ┌─Connection-DM10─┐                                          │
│ ├────────┴─┴─────────────────┴──────────────────────────────────────────┤
│ │                                                                       │
│ │  ══ TABLES TAB (Default) ══                                           │
│ │  ┌──Table Selection────────────────┐ ┌──Copy Strategy────────────────┐│
│ │  │ Tables to Copy:                 │ │ Default Records: [10000]      ││
│ │  │ ┌────────────────────────────┐  │ │                               ││
│ │  │ │ *                          │  │ │ Strategy Overrides: [?]       ││
│ │  │ │                            │  │ │ ┌───────────────────────────┐ ││
│ │  │ └────────────────────────────┘  │ │ │ CUSTTABLE:5000            │ ││
│ │  │ Tables to Exclude:              │ │ │ SALESLINE:days:30         │ ││
│ │  │ ┌────────────────────────────┐  │ │ └───────────────────────────┘ ││
│ │  │ │ Sys*                       │  │ │                               ││
│ │  │ │ Batch*                     │  │ │ Fields to Exclude:            ││
│ │  │ └────────────────────────────┘  │ │ ┌───────────────────────────┐ ││
│ │  └─────────────────────────────────┘ │ │ SYSROWVERSION             │ ││
│ │                                      │ └───────────────────────────┘ ││
│ │                                      └────────────────────────────────┘│
│ └───────────────────────────────────────────────────────────────────────┤
├─────────────────────────────────────────────────────────────────────────┤
│ ┌─Tables─┐ ┌─Connection-DM10─┐                                          │
│ ├────────┴─┴─────────────────┴──────────────────────────────────────────┤
│ │                                                                       │
│ │  ══ CONNECTION TAB ══                                                 │
│ │                                                                       │
│ │  Alias: [DM10                    ]                                    │
│ │  ────────────────────────────────────────────────────────────────     │
│ │  Tier2 Settings                                                       │
│ │  Server\Database: [spartan-srv-oce-d365...database.windows.net\db_...]│
│ │  Username:        [admin                  ]                           │
│ │  Password:        [********               ]                           │
│ │  Connection Timeout (s): [3   ]                                       │
│ │  Command Timeout (s):    [600 ]                                       │
│ │  ────────────────────────────────────────────────────────────────     │
│ │  AxDB Settings                                                        │
│ │  Server\Database: [localhost\AxDB         ]                           │
│ │  Username:        [sa                     ]                           │
│ │  Password:        [********               ]                           │
│ │  Command Timeout (s):    [0   ]                                       │
│ │  ────────────────────────────────────────────────────────────────     │
│ │  Execution Settings                                                   │
│ │  Parallel Fetch Connections:  [10 ]                                   │
│ │  Parallel Insert Connections: [10 ]                                   │
│ │                                                                       │
│ └───────────────────────────────────────────────────────────────────────┤
├─────────────────────────────────────────────────────────────────────────┤
│ [Prepare Table List] [Get Data] [Insert Data] [Insert Failed]           │
│ [Run All] [Stop]                                                        │
├─────────────────────────────────────────────────────────────────────────┤
│ Stage 2/3: Get Data - 150/2000 tables                                   │
├─────────────────────────────────────────────────────────────────────────┤
│ ┌─Table List────────────────────────────────────────────────────────────┤
│ │ Table Name  │TableID│Strategy   │Tier2 Rows│GB   │Status  │Rec...    ││
│ ├─────────────┼───────┼───────────┼──────────┼─────┼────────┼──────────┤│
│ │ CUSTTABLE   │ 10878 │RecId:5000 │ 125,000  │ 0.5 │Fetched │ 5000 ││
│ │ SALESLINE   │ 10234 │Days:30    │ 5,000,000│ 12.3│Fetching│      ││
│ │ INVENTTRANS │ 10456 │RecId:10000│ 8,000,000│ 25.1│Pending │      ││
│ └─────────────────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────────┤
│ Loaded 198 tables, 7 failed                                         │
├─────────────────────────────────────────────────────────────────────┤
│ ┌─Log──────────────────────────────────────────────────────[Clear]─┐│
│ │ [10:30:15] Starting Prepare Table List...                        ││
│ │ [10:30:16] Table MYTABLE not found in AxDB SQLDICTIONARY, skip   ││
│ │ [10:30:18] Prepared 2000 tables                                  ││
│ │ ─────────────────────────────────────────────────────────────────││
│ │ [10:30:20] Starting Get Data...                                  ││
│ └──────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────┘
```

### 6.2 UI Components

#### 6.2.1 Configuration Panel (Top Bar)

| Component | Type | Notes |
|-----------|------|-------|
| Config Dropdown | ComboBox | Lists all configs in Config folder |
| Save Button | Button | Saves to current config |
| Save As Button | Button | Prompts for new name; defaults to current Alias |
| Load Button | Button | Refreshes dropdown, loads selected |

#### 6.2.2 Tab Control

| Tab | Title | Notes |
|-----|-------|-------|
| Tables Tab | "Tables" | Default/first tab; static title |
| Connection Tab | "Connection-{Alias}" | Title updates dynamically when Alias changes |

#### 6.2.3 Connection Tab Components

| Component | Type | Notes |
|-----------|------|-------|
| Alias | TextBox | Max 30 chars; updates tab title on change |
| Tier2 Server\Database | TextBox | Combined server and database |
| Tier2 Username | TextBox | Standard input |
| Tier2 Password | TextBox | PasswordChar = '*' |
| Tier2 Connection Timeout | NumericUpDown | Default: 3 |
| Tier2 Command Timeout | NumericUpDown | Default: 600 |
| AxDB Server\Database | TextBox | Default: localhost\AxDB |
| AxDB Username | TextBox | Standard input |
| AxDB Password | TextBox | PasswordChar = '*' |
| AxDB Command Timeout | NumericUpDown | Default: 0 |
| Parallel Fetch Connections | NumericUpDown | Min: 1, Max: 50, Default: 10 |
| Parallel Insert Connections | NumericUpDown | Min: 1, Max: 50, Default: 10 |

#### 6.2.4 Tables Tab - Table Selection Panel

| Component | Type | Notes |
|-----------|------|-------|
| Tables to Copy | TextBox | Multiline, accepts wildcards |
| Tables to Exclude | TextBox | Multiline, accepts wildcards |
| Fields to Exclude | TextBox | Multiline, exact match |

#### 6.2.5 Tables Tab - Copy Strategy Panel

| Component | Type | Notes |
|-----------|------|-------|
| Default Records | NumericUpDown | Min: 1, Max: 1000000 |
| Strategy Overrides Label | Label | Has tooltip with format/examples (see below) |
| Strategy Overrides | TextBox | Multiline, parsed format |

**Strategy Overrides Tooltip:**
```
Format:
  TableName:RecordCount     (RecId strategy)
  TableName:days:DayCount   (ModifiedDate strategy)

Examples:
  CUSTTABLE:5000
  SALESLINE:days:30
```

#### 6.2.6 Action Buttons

| Button | Enabled When |
|--------|-------------|
| Prepare Table List | Not executing |
| Get Data | Table list exists, not executing |
| Insert Data | Fetched data exists, not executing |
| Insert Failed | Tables with InsertError exist, cache valid, not executing |
| Run All | Not executing |
| Stop | Executing |

#### 6.2.7 Data Grid

- Standard DataGridView with DataSource binding
- All columns sortable
- Columns auto-sized to content
- Selection mode: FullRowSelect
- Read-only

**Context Menu:**
- **Copy Table Name**: Copies the selected table name to clipboard
- **Get SQL**: Generates formatted SQL for the selected table(s) and copies to clipboard

**Get SQL Feature:**

When "Get SQL" is selected from the context menu (after "Prepare Table List" has been run), the tool generates formatted SQL showing:

```sql
-- ============================================
-- Table: CUSTTABLE
-- Strategy: RecId:5000 WHERE
-- Cleanup: Delete by WHERE + RecId
-- ============================================

-- === SOURCE QUERY (Tier2) ===
SELECT TOP 5000 [RECID], [ACCOUNTNUM], [DATAAREAID], ...
FROM [CUSTTABLE]
WHERE DATAAREAID='1000'
ORDER BY RECID DESC

-- === CLEANUP QUERIES (AxDB) ===
-- Step 1: Delete by custom WHERE condition
DELETE FROM [CUSTTABLE]
WHERE DATAAREAID='1000'

-- Step 2: Delete by RecId (>= minimum from source)
DELETE FROM [CUSTTABLE]
WHERE RECID >= @MinRecId
-- Note: @MinRecId will be determined after fetching source data

-- === INSERT ===
-- SqlBulkCopy will be used to insert fetched records

-- === SEQUENCE UPDATE ===
DECLARE @MaxRecId BIGINT = (SELECT MAX(RECID) FROM [CUSTTABLE])
DECLARE @TableId INT = 10878 -- Actual TableId from SQLDICTIONARY
IF @MaxRecId > (SELECT CAST(current_value AS BIGINT) FROM sys.sequences WHERE name = 'SEQ_10878')
    ALTER SEQUENCE [SEQ_10878] RESTART WITH @MaxRecId
```

This feature allows developers to:
- Test SQL logic before running operations
- Verify cleanup strategies are correct
- Debug complex WHERE clause scenarios
- Document the SQL that will be executed

**Columns:**

| Column | Width | Alignment |
|--------|-------|-----------|
| Table Name | 150 | Left |
| TableID | 80 | Right |
| Strategy | 100 | Left |
| Tier2 Rows | 100 | Right |
| Tier2 Size (GB) | 80 | Right |
| Status | 100 | Center |
| Records Fetched | 100 | Right |
| Min RecId | 120 | Right |
| Fetch Time (s) | 80 | Right |
| Insert Time (s) | 80 | Right |
| Error | 200 | Left |

#### 6.2.8 Status Line

- Label showing current operation status
- Updated after each stage completes
- Examples:
  - "Ready"
  - "Stage 1/3: Prepare Table List"
  - "Stage 2/3: Get Data - 150/2000 tables"
  - "Completed: 1950 inserted, 50 failed"

#### 6.2.9 Log Panel

- Multiline TextBox, read-only
- Vertical scrollbar
- Clear button
- Auto-scroll to bottom on new entries
- Timestamp format: [HH:mm:ss]
- Separator line between stages: "─────────────────"
- Retained between stages (not cleared automatically)

### 6.3 UI Behavior

#### 6.3.1 Real-Time Updates

- Table list updates every 500ms during execution
- Status line updates on each table completion
- Log updates immediately on events

#### 6.3.2 Error Display

- Error column shows truncated message (first 50 chars)
- Full error shown in tooltip on hover
- Error also logged to log panel with full details

#### 6.3.3 Input Validation

- Connection validation only on operation attempt
- Strategy syntax validation on "Prepare Table List"
- Config name validation on "Save As"

---

## 7. Error Handling

### 7.1 Connection Errors

| Scenario | Behavior |
|----------|----------|
| Initial connection failure | Show error dialog, abort operation |
| Connection drop during execution | Cancel all workers, log error, update status |
| Authentication failure | Show error dialog with details |

### 7.2 Data Errors

| Scenario | Behavior |
|----------|----------|
| Table not in SQLDICTIONARY | Skip table, log to UI |
| Strategy syntax error | Stop execution, show error dialog |
| ModifiedDate column missing | Set table status to error, continue with others |
| Fetch error | Set table status to FetchError, continue with others |
| Insert error | Re-enable triggers, set status to InsertError, continue |

### 7.3 Configuration Errors

| Scenario | Behavior |
|----------|----------|
| Config file corrupted | Show error, load defaults |
| Invalid config name | Show validation error, prevent save |
| Missing Config folder | Create automatically |

---

## 8. Data Structures

### 8.1 TableInfo Class

```csharp
public class TableInfo
{
    // Identification
    public string TableName { get; set; }
    public int TableId { get; set; }

    // Strategy (Extended)
    public CopyStrategyType StrategyType { get; set; }
    public int StrategyValue { get; set; }  // Backward compatibility
    public int? RecIdCount { get; set; }    // For RecId strategy
    public int? DaysCount { get; set; }     // For ModifiedDate strategy
    public string WhereClause { get; set; } // Custom WHERE condition (without WHERE keyword)
    public bool UseTruncate { get; set; }   // -truncate flag

    // Tier2 Info
    public long Tier2RowCount { get; set; }
    public decimal Tier2SizeGB { get; set; }
    public string FetchSql { get; set; }

    // Field Info
    public List<string> CopyableFields { get; set; }

    // Execution State
    public TableStatus Status { get; set; }
    public int RecordsFetched { get; set; }
    public long MinRecId { get; set; }
    public decimal FetchTimeSeconds { get; set; }
    public decimal InsertTimeSeconds { get; set; }
    public string Error { get; set; }

    // Cached Data (not persisted)
    public DataTable CachedData { get; set; }
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
```

### 8.2 AppConfiguration Class

```csharp
public class AppConfiguration
{
    public string ConfigName { get; set; }
    public DateTime LastModified { get; set; }
    public string Alias { get; set; }  // Max 30 chars, used for tab title and default save name

    public ConnectionSettings Tier2Connection { get; set; }
    public ConnectionSettings AxDbConnection { get; set; }

    public string TablesToInclude { get; set; }  // Multiline
    public string TablesToExclude { get; set; }  // Multiline
    public string SystemExcludedTables { get; set; }  // Multiline, system-level exclusions
    public string FieldsToExclude { get; set; }  // Multiline

    public int DefaultRecordCount { get; set; }
    public string StrategyOverrides { get; set; }  // Multiline, pipe-delimited format

    public int ParallelFetchConnections { get; set; }
    public int ParallelInsertConnections { get; set; }
}

public class ConnectionSettings
{
    public string ServerDatabase { get; set; }  // Combined format: server\database
    public string Username { get; set; }
    public string Password { get; set; }  // Obfuscated in JSON
    public int ConnectionTimeout { get; set; }  // Only for Tier2
    public int CommandTimeout { get; set; }
    
    // Helper method to parse ServerDatabase
    public (string Server, string Database) ParseServerDatabase()
    {
        var parts = ServerDatabase?.Split('\\', 2) ?? new string[0];
        return (
            parts.Length > 0 ? parts[0] : "",
            parts.Length > 1 ? parts[1] : ""
        );
    }
}
```

---

## 9. Security Considerations

### 9.1 Password Handling

- Passwords stored with Base64 obfuscation (not encryption)
- Passwords displayed as asterisks in UI
- Config files should be protected by file system permissions

### 9.2 SQL Injection Prevention

- All table/field names validated against SQLDICTIONARY
- Only uppercase alphanumeric names allowed
- Use parameterized queries where possible
- Dynamic SQL for table names uses bracket escaping: `[@TableName]`

### 9.3 Connection Security

- SQL authentication only (no Windows auth)
- Azure SQL connections should use TLS (default)

---

## 10. Performance Considerations

### 10.1 Memory Management

- Each table's data stored as DataTable in memory
- Expected total memory: 2-4 GB for typical workload
- No explicit memory limits; relies on system resources

### 10.2 Parallel Execution

- Semaphore-based worker limiting
- Connection pool shared across workers
- Cancellation token for immediate stop

### 10.3 Bulk Insert Optimization

- SqlBulkCopy with table lock
- Batch size: 10,000 rows
- Triggers disabled during insert

---

## 11. Future Enhancements (Out of Scope)

The following features are noted for potential future implementation:

1. State persistence for resume after application close
2. File-based logging
3. Progress bar visualization
4. Automatic retry with configurable attempts
5. Table dependency ordering
6. Delta sync (incremental updates)
7. Schema comparison report
8. Scheduled execution

---

## Appendix A: Default Configuration Values

```json
{
  "configName": "Default",
  "alias": "default",
  "tier2Connection": {
    "serverDatabase": "",
    "username": "",
    "password": "",
    "connectionTimeout": 3,
    "commandTimeout": 600
  },
  "axDbConnection": {
    "serverDatabase": "localhost\\AxDB",
    "username": "",
    "password": "",
    "commandTimeout": 0
  },
  "tablesToInclude": "*",
  "tablesToExclude": "Sys*\nBatch*",
  "fieldsToExclude": "SYSROWVERSION",
  "defaultRecordCount": 10000,
  "strategyOverrides": "",
  "parallelFetchConnections": 10,
  "parallelInsertConnections": 10
}
```

---

## Appendix B: SQL Query Reference

### B.1 Table Discovery

```sql
SELECT 
    o.name AS TableName,
    MAX(s.row_count) AS RowCount,
    SUM(s.reserved_page_count) * 8.0 / (1024 * 1024) AS SizeGB,
    CASE 
        WHEN MAX(s.row_count) > 0 
        THEN (8 * 1024 * SUM(s.reserved_page_count)) / MAX(s.row_count) 
        ELSE 0 
    END AS BytesPerRow
FROM sys.dm_db_partition_stats s
INNER JOIN sys.objects o ON o.object_id = s.object_id
WHERE o.type = 'U'
GROUP BY o.name
HAVING MAX(s.row_count) > 0
ORDER BY SizeGB DESC
```

### B.2 Get TableID

```sql
SELECT TableID 
FROM SQLDICTIONARY 
WHERE UPPER(name) = UPPER(@TableName) 
  AND FIELDID = 0
```

### B.3 Get Fields

```sql
SELECT SQLName 
FROM SQLDICTIONARY 
WHERE TableID = @TableId 
  AND FIELDID <> 0
```

### B.4 Fetch by RecId

```sql
SELECT TOP (@RecordCount) [Field1], [Field2], ... 
FROM [@TableName] 
ORDER BY RecId DESC
```

### B.5 Fetch by ModifiedDate

```sql
SELECT [Field1], [Field2], ... 
FROM [@TableName] 
WHERE [MODIFIEDDATETIME] > @CutoffDateTime
```

### B.6 Delete by RecId

```sql
DELETE FROM [@TableName] 
WHERE RecId >= @MinRecId
```

### B.7 Delete by ModifiedDate

```sql
DELETE FROM [@TableName] 
WHERE [MODIFIEDDATETIME] >= @MinModifiedDate

DELETE FROM [@TableName] 
WHERE RecId IN (SELECT RecId FROM @FetchedRecIds)
```

### B.8 Disable/Enable Triggers

```sql
ALTER TABLE [@TableName] DISABLE TRIGGER ALL
ALTER TABLE [@TableName] ENABLE TRIGGER ALL
```

### B.9 Update Sequence

```sql
DECLARE @MaxRecId BIGINT = (SELECT MAX(RecId) FROM [@TableName])
DECLARE @CurrentSeq BIGINT = (
    SELECT CAST(current_value AS BIGINT) 
    FROM sys.sequences 
    WHERE name = 'SEQ_' + CAST(@TableId AS VARCHAR)
)

IF @MaxRecId > @CurrentSeq
    EXEC('ALTER SEQUENCE [SEQ_' + @TableId + '] RESTART WITH ' + @MaxRecId)
```

---

*End of Specification*
