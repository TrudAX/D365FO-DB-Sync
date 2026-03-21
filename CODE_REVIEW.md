# D365FO-DB-Sync — Code Review

Reviewed source files: all `.cs` files under `DBSyncTool/`.  
Date: 2026-03-21

---

## 🔴 Critical

### 1 · No transaction in standard-mode `InsertDataAsync` — data corruption on failure
**File:** `Services/AxDbDataService.cs` ~line 103  

`InsertDataAsync` opens a plain `SqlConnection` with no transaction.  
The delete step (`DeleteExistingRecordsAsync` / `DeleteByRecIdListAsync`) commits immediately; the subsequent `SqlBulkCopy` insert is a separate, unprotected operation.  
If the bulk insert fails (network timeout, constraint violation, cancellation), the rows have already been deleted and cannot be recovered. The table is left empty or partially populated.  
The **incremental-mode** path in `CopyOrchestrator.ProcessTableIncrementalModeAsync` correctly wraps both steps in a transaction — the standard mode does not.

```csharp
// AxDbDataService.cs ~L103
using var connection = new SqlConnection(_connectionString);
await connection.OpenAsync(cancellationToken);
// ...
await DeleteExistingRecordsAsync(tableInfo, connection, null, cancellationToken); // committed
// ...
await bulkCopy.WriteToServerAsync(dataToInsert, cancellationToken); // can fail → table empty
```

**Impact:** Silent permanent data loss when any insert-phase error occurs in standard mode.

---

### 2 · `TimestampManager` and `MaxRecIdManager` are not thread-safe
**Files:** `Helpers/TimestampManager.cs` line 10–11; `Helpers/MaxRecIdManager.cs`; `Services/CopyOrchestrator.cs` ~lines 743, 805, 975  

Both managers use plain `Dictionary<string, …>`. Multiple parallel workers (up to 50 by config) each call `SetTimestamps`, `SaveToConfig`, and `OnTimestampsUpdated` after completing their table — with no lock:

```csharp
// TimestampManager.cs
private Dictionary<string, byte[]> _tier2Timestamps = new();
private Dictionary<string, byte[]> _axdbTimestamps = new();

// CopyOrchestrator.cs — called concurrently from N parallel worker tasks
_timestampManager.SetTimestamps(table.TableName, tier2Max, axdbMax);
_timestampManager.SaveToConfig(_config);
OnTimestampsUpdated();
```

Concurrent writes to a non-thread-safe `Dictionary` cause undefined behaviour: corrupted entries, missing timestamps, or thrown `InvalidOperationException` during enumeration in `SaveToConfig` → `FormatTimestampText`.

**Impact:** Timestamp data silently corrupted or lost; subsequent runs fall back to full-table mode; intermittent crashes under parallel load.

---

### 3 · Integer overflow in record count parsing
**File:** `Services/CopyOrchestrator.cs` ~line 1464–1466  

```csharp
private static bool TryParseRecordCount(string input, out int count)
{
    if (input.EndsWith("m", StringComparison.OrdinalIgnoreCase))
    {
        string numericPart = input.Substring(0, input.Length - 1);
        if (int.TryParse(numericPart, out int millions))
        {
            count = millions * 1_000_000;  // overflows for millions >= 2148
            return true;
        }
```

`int.MaxValue` is 2,147,483,647. For `millions = 2148`, the multiplication yields 2,148,000,000, which silently wraps to a large negative number. A strategy like `BIGTABLE|3000m` produces an invalid `SELECT TOP (-3000000000)` query.

**Impact:** Silent wrong record count; unexpected SQL errors or reading far more rows than intended.

---

### 4 · SQL injection in backup `NAME` parameter
**File:** `Services/BackupService.cs` ~lines 55–60  

```csharp
string safeAlias = (alias ?? "default").Replace("'", "''");
string backupName = $"{safeAlias}_{formattedDateTime}-Full Database Backup";
string sql = $"BACKUP DATABASE [{database}] TO DISK = @path " +
             $"WITH COPY_ONLY, NOFORMAT, INIT, NAME = N'{backupName}', ...";
```

Only single-quotes are escaped. An alias containing `--` makes the rest of the line a SQL comment, silently stripping all `WITH` options. The `database` parameter is embedded inside `[{database}]` brackets without sanitising the `]` character: an alias containing `]` breaks the identifier quoting and allows injection.

**Impact:** Malformed or malicious backup commands; potential data loss if `INIT` or other options are stripped.

---

## 🟠 High

### 5 · `SEQUENCE_GAP` is 10,000 in `AxDbDataService` but only 10 in `CopyOrchestrator`
**Files:** `Services/AxDbDataService.cs` lines 11, 431; `Services/CopyOrchestrator.cs` line 1035  

```csharp
// AxDbDataService.cs
private const int SEQUENCE_GAP = 10000;
long newSeq = Math.Max(maxRecId, currentSeq) + SEQUENCE_GAP;   // +10,000

// CopyOrchestrator.cs — incremental mode
long newSeq = Math.Max(maxRecId, currentSeq) + 10;             // +10 only
```

A gap of 10 is too small. Any D365 background process (batch jobs, posting operations) that inserts even a handful of records between the sync and the next RecId sequence read will exhaust the gap and cause a sequence collision (`UNIQUE constraint` or duplicate RecId errors in the live D365 environment).

**Impact:** RecId collisions on the local AxDB after incremental-mode runs; potential data integrity errors.

---

### 6 · `CancellationTokenSource` never disposed before replacement
**File:** `Services/CopyOrchestrator.cs` ~lines 47, 245, 345, 539  

`CancellationTokenSource` implements `IDisposable` and holds OS wait handles. A new instance is assigned to `_cancellationTokenSource` on every call to the four main entry-point methods without disposing the previous one:

```csharp
public async Task PrepareTableListAsync()
{
    _cancellationTokenSource = new CancellationTokenSource(); // previous one leaked
```

**Impact:** OS handle leak accumulates across repeated Discover / Process / Retry cycles in a long-running session.

---

### 7 · `tier2Data` DataTable leaked on exception in incremental mode
**File:** `Services/CopyOrchestrator.cs` ~lines 899–954  

```csharp
DataTable tier2Data = await _tier2Service.FetchDataByTimestampAsync(...);
// ... several operations that can throw ...
using (DataTable filteredData = tier2Data.Clone()) { ... }
tier2Data.Dispose();  // only reached on success
```

`tier2Data` is not wrapped in a `using` statement. Any exception thrown between the fetch and the explicit `.Dispose()` call leaks the entire DataTable (potentially hundreds of MB) for the lifetime of the session.

**Impact:** Growing memory consumption during error scenarios; OOM risk in long syncs with intermittent failures.

---

### 8 · `COUNT(*)` overflows for tables with more than ~2.1 billion rows
**File:** `Services/AxDbDataService.cs` lines 910, 926  

```csharp
string sql = $"SELECT COUNT(*) FROM [{tableName}]";
// ...
return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
```

`COUNT(*)` returns SQL `int` (max 2,147,483,647). For very large D365 tables, the value overflows to a negative `int` on the SQL Server side; `Convert.ToInt64` then returns a negative number, making every threshold comparison wrong. `COUNT_BIG(*)` returns `bigint` and has no overflow risk.

**Impact:** Wrong row counts for large tables; incorrect TRUNCATE vs. INCREMENTAL mode selection; tables processed in the wrong mode.

---

### 9 · `LogDirect` incorrectly decrements the pending-log counter on direct UI-thread calls
**File:** `MainForm.cs` ~lines 494–498  

`LogDirect` is called both via `BeginInvoke` (counter was incremented) and directly from the UI thread (counter was not incremented). In the direct-call path, the guard `if (_pendingLogCount > 0)` can still decrement a count that belongs to a background-thread message, allowing one extra background log through the throttle and drifting `_pendingLogCount` negative over time.

**Impact:** Log throttle logic is unreliable; UI can become unresponsive if the counter diverges significantly.

---

### 10 · `GetRecIdSetAsync` performs an unbounded full table scan into memory
**File:** `Services/AxDbDataService.cs` line 945  

```csharp
string sql = $"SELECT RecId FROM [{tableName}]";
```

No `WHERE` clause, no range filter, no `TOP` limit. This loads **every RecId** in the table into a .NET `HashSet<long>`. For a table with 50 million rows (8 bytes each), that is ~400 MB of RAM plus network transfer, inside the incremental-mode transaction for every table being processed.

**Impact:** Excessive memory usage and network traffic; can stall or OOM the application on large tables.

---

### 11 · `UpdateTablesGrid` uses synchronous `Invoke`, blocking parallel worker threads
**File:** `MainForm.cs` ~line 535  

```csharp
Invoke(new Action<List<TableInfo>>(UpdateTablesGrid), tables);
```

`Invoke` is synchronous — the calling worker thread blocks until the UI thread finishes painting the grid. With up to 10 parallel workers each firing `OnTablesUpdated()`, all workers can simultaneously stall waiting for UI repaints. `BeginInvoke` (fire-and-forget async) is the correct pattern here, matching how `Log` is implemented.

**Impact:** Parallel throughput degraded; workers blocked during UI updates reduce effective parallelism.

---

## 🟡 Medium

### 12 · `*` replacement is too broad; corrupts SQL templates containing literals
**Files:** `Services/CopyOrchestrator.cs` ~line 1622; `Services/Tier2DataService.cs` ~line 224  

```csharp
string sql = strategy.SqlTemplate
    .Replace("@recordCount", recordCount.ToString())
    .Replace("*", fieldList);   // replaces ALL asterisks
```

A template such as:
```sql
SELECT * FROM MYTABLE WHERE DESCRIPTION LIKE '%*%'
```
would produce corrupted SQL after `*` is replaced with the full field list. The same problem exists in `FetchControlDataAsync` where `*` is replaced with `RecId, SysRowVersion`. Any `*` inside a string literal, `COUNT(*)`, or a comment is also replaced.

**Impact:** Silently broken SQL for any user-defined strategy containing `*` in a non-SELECT-list position.

---

### 13 · Threshold comparison is `>` in one place and `>=` in another
**Files:** `Services/CopyOrchestrator.cs` ~line 661; `Services/AxDbDataService.cs` ~line 200  

```csharp
// CopyOrchestrator.cs
bool useTruncate = changePercent > _config.TruncateThresholdPercent || ...;

// AxDbDataService.cs
if (changePercent >= truncateThreshold)
```

At exactly the configured threshold value (default 40%), the optimised path (CopyOrchestrator) decides "no truncate" while the standard path (AxDbDataService) decides "truncate." The same table in the same state can behave differently depending on which code path is active.

**Impact:** Inconsistent mode selection at the boundary; unpredictable behaviour that is hard to debug.

---

### 14 · Missing-records path in incremental mode runs two full RecId table scans
**File:** `Services/CopyOrchestrator.cs` ~lines 776–854  

When zero changes are detected but missing records exist:
1. The code opens a read-only connection and fetches ALL RecIds from the AxDB table (full scan).
2. It identifies which RecIds are missing.
3. It then falls through to the main branch at ~line 818 where `GetRecIdSetAsync` is called **again** inside the transaction — a second full table scan.

The first scan's result is discarded. For large tables, this doubles network traffic and memory usage for this recovery path.

**Impact:** Unnecessary 2× resource cost for the missing-records edge case on large tables.

---

### 15 · `ConfigManager` writes config file non-atomically
**File:** `Helpers/ConfigManager.cs` ~line 73  

```csharp
File.WriteAllText(filePath, json);
```

If the process crashes mid-write (power loss, OS kill), the config file is left partially written and unreadable by the JSON deserialiser on next start. All connection settings, table strategies, and stored timestamps would be lost.

**Impact:** Permanent config corruption on abnormal process termination; no recovery path without manual file restoration.

---

### 16 · `adapter.Fill()` is synchronous inside an async method
**File:** `Services/Tier2DataService.cs` ~line 181  

```csharp
using var adapter = new SqlDataAdapter(command);
adapter.Fill(dataTable);  // synchronous — blocks a thread-pool thread
```

All other fetch methods use `ExecuteReaderAsync`. This call blocks a thread-pool thread for the entire duration of the SQL query, reducing parallel throughput and potentially contributing to thread-pool starvation under heavy load.

**Impact:** Reduced scalability; inconsistent async pattern with the rest of the codebase.

---

### 17 · `CloneAndObfuscate` round-trips JSON with default (PascalCase) serialiser options
**File:** `Helpers/ConfigManager.cs` ~lines 207–208  

```csharp
string json = JsonSerializer.Serialize(config);             // default options
var clone = JsonSerializer.Deserialize<AppConfiguration>(json)!;
```

`SaveConfiguration` uses `JsonNamingPolicy.CamelCase`. `CloneAndObfuscate` uses the default (PascalCase). If any property name survives both serialisers correctly by coincidence today, a future property rename could cause silent data loss during the clone (the property deserialises to its default value instead of the original).

**Impact:** Fragile pattern; could silently lose configuration fields in future.

---

### 18 · `ProcessSingleTableByNameAsync` status check and reset are not atomic
**File:** `Services/CopyOrchestrator.cs` ~lines 507–535  

```csharp
if (table.Status == TableStatus.Fetching || table.Status == TableStatus.Inserting)
{
    _logger("Table is currently being processed");
    return;
}
table.Status = TableStatus.Pending;  // not atomic with the check above
```

There is no lock between the status check and the reset. A concurrent parallel worker that changes the status between the check and the reset could allow both to process the same table simultaneously. (The UI's `_isExecuting` guard limits exposure, but the race is real if called from code.)

**Impact:** Potential duplicate processing of the same table in concurrent scenarios.

---

## 🔵 Low

### 19 · Password "obfuscation" uses plain Base64
**File:** `Helpers/EncryptionHelper.cs`  

Passwords are stored as Base64-encoded strings in the JSON config files. Anyone with read access to the `Config/` folder can decode all passwords trivially using any Base64 decoder. The class comment acknowledges this, but users may not be aware.

**Impact:** Credentials exposed to any user or process that can read the config directory.

---

### 20 · "Get SQL" preview omits `SEQUENCE_GAP`, misleading for manual execution
**File:** `MainForm.cs` ~lines 1122–1123  

The generated SQL preview shows:
```sql
ALTER SEQUENCE [SEQ_12345] RESTART WITH @MaxRecId
```
but the actual runtime adds `+ SEQUENCE_GAP` (10,000). A developer who copies the preview SQL for manual execution would set the sequence 10,000 lower than the tool would, increasing the risk of RecId collisions.

**Impact:** Misleading developer tool; manual execution of preview SQL is subtly incorrect.

---

### 21 · `TruncateWithFallbackAsync` catches TRUNCATE error by message text
**File:** `Services/AxDbDataService.cs` ~line 369  

```csharp
catch (SqlException ex) when (ex.Message.Contains("Cannot TRUNCATE TABLE"))
```

SQL Server error messages vary by locale and version. Using `ex.Number` (SQL error code 4712 or 3732) is more reliable and version-independent.

**Impact:** The fallback may not trigger correctly on non-English SQL Server installations or future SQL Server versions.

---

### 22 · `UpdateChecker` creates a new `HttpClient` on every call
**File:** `Helpers/UpdateChecker.cs` ~line 38  

```csharp
using var client = new HttpClient();
```

`HttpClient` should be reused (static instance or `IHttpClientFactory`) to avoid TIME_WAIT socket accumulation. For a single call per session, the impact is minimal, but it is a known anti-pattern.

**Impact:** Minor socket resource waste; negligible in practice for a once-per-session call.

---

### 23 · `ConnectionSettings.BuildConnectionString` accepts empty username/password silently
**File:** `Models/ConnectionSettings.cs` ~line 27  

Only `server` and `database` are validated as non-empty. An empty `Username` or `Password` produces a cryptic authentication failure at connection time rather than a clear validation error at configuration time.

**Impact:** Poor user experience; authentication failures reported without identifying the root cause.

---

### 24 · `-truncate` suffix stripping uses a magic length constant
**File:** `Services/CopyOrchestrator.cs` ~line 1395  

```csharp
workingLine = workingLine.Substring(0, workingLine.Length - 10).Trim();
```

The hard-coded `10` is the length of ` -truncate`. This is correct today but brittle: a future change to the flag name (capitalisation, extra space) that isn't reflected here would silently leave the suffix in the table name or strategy string.

**Impact:** Low; currently correct but fragile against future maintenance changes.

---

## Summary Table

| # | File | Approx. Line | Severity | Issue |
|---|------|-------------|----------|-------|
| 1 | `AxDbDataService.cs` | ~103 | 🔴 Critical | No transaction wrapping delete + bulk insert in standard mode |
| 2 | `TimestampManager.cs` / `CopyOrchestrator.cs` | ~10, ~743 | 🔴 Critical | Non-thread-safe `Dictionary` written by parallel workers |
| 3 | `CopyOrchestrator.cs` | ~1464 | 🔴 Critical | Integer overflow: `millions * 1_000_000` exceeds `int.MaxValue` |
| 4 | `BackupService.cs` | ~55–60 | 🔴 Critical | Backup `NAME` embedded in SQL without full sanitisation |
| 5 | `AxDbDataService.cs` / `CopyOrchestrator.cs` | ~431, ~1035 | 🟠 High | `SEQUENCE_GAP` is 10,000 in one place and 10 in the other |
| 6 | `CopyOrchestrator.cs` | ~47, 245, 345, 539 | 🟠 High | `CancellationTokenSource` never disposed before replacement |
| 7 | `CopyOrchestrator.cs` | ~899–954 | 🟠 High | `tier2Data` DataTable leaked on exception in incremental mode |
| 8 | `AxDbDataService.cs` | ~910, 926 | 🟠 High | `COUNT(*)` overflows for tables > 2.1 B rows; use `COUNT_BIG(*)` |
| 9 | `MainForm.cs` | ~494–498 | 🟠 High | `LogDirect` decrements counter on direct UI-thread calls |
| 10 | `AxDbDataService.cs` | ~945 | 🟠 High | `GetRecIdSetAsync` full table scan into memory with no filter |
| 11 | `MainForm.cs` | ~535 | 🟠 High | Synchronous `Invoke` blocks all parallel worker threads on UI update |
| 12 | `CopyOrchestrator.cs` / `Tier2DataService.cs` | ~1622, ~224 | 🟡 Medium | `*` replacement too broad; corrupts SQL templates with literals |
| 13 | `CopyOrchestrator.cs` / `AxDbDataService.cs` | ~661, ~200 | 🟡 Medium | Threshold: `>` vs `>=` inconsistency at the boundary value |
| 14 | `CopyOrchestrator.cs` | ~776–854 | 🟡 Medium | Missing-records path runs two full RecId table scans |
| 15 | `ConfigManager.cs` | ~73 | 🟡 Medium | Non-atomic config write; crash mid-write permanently corrupts config |
| 16 | `Tier2DataService.cs` | ~181 | 🟡 Medium | `adapter.Fill()` is synchronous inside an async method |
| 17 | `ConfigManager.cs` | ~207–208 | 🟡 Medium | Clone round-trip uses default JSON options, not camelCase |
| 18 | `CopyOrchestrator.cs` | ~507–535 | 🟡 Medium | Status check and reset not atomic (theoretical race condition) |
| 19 | `EncryptionHelper.cs` | entire file | 🔵 Low | Base64 is not encryption; config files expose passwords in plaintext |
| 20 | `MainForm.cs` | ~1122–1123 | 🔵 Low | "Get SQL" preview omits `SEQUENCE_GAP`; misleads manual users |
| 21 | `AxDbDataService.cs` | ~369 | 🔵 Low | TRUNCATE fallback caught by message text, not by error number |
| 22 | `UpdateChecker.cs` | ~38 | 🔵 Low | `HttpClient` created per call (socket-exhaustion anti-pattern) |
| 23 | `ConnectionSettings.cs` | ~27 | 🔵 Low | Empty `Username`/`Password` not validated at build time |
| 24 | `CopyOrchestrator.cs` | ~1395 | 🔵 Low | `-truncate` strip uses magic length constant `10` |
