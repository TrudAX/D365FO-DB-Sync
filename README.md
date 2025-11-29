# D365FO Database Copy Tool

A WinForms .NET 9 application for copying data from Dynamics 365 Finance & Operations Azure SQL Database (Tier2) to local SQL Server (AxDB).

## Overview

This tool helps developers synchronize data from D365FO cloud environments to their local development databases, making it easier to test with production-like data.

![](assets/MainDialog.png)

## Features

### Core Functionality
- **Selective Table Copying**: Use patterns to include/exclude tables (e.g., `CUST*`, `Sys*`)
- **Advanced Copy Strategies**:
  - **RecId Strategy**: Copy last N records by RecId (e.g., last 10,000 records)
  - **ModifiedDate Strategy**: Copy records modified in last N days
  - **WHERE Clause Strategy**: Copy records matching custom SQL conditions
  - **Full Table Copy**: Copy entire table with automatic truncate
  - **Combined Strategies**: Mix RecId/Days with WHERE clauses for precise filtering
  - **Truncate Option**: Force truncate before insert with `-truncate` flag
- **Smart Field Mapping**: Automatically maps common fields between source and destination
- **Parallel Execution**: Configurable parallel fetch and insert operations for performance

### Extended Strategy Format

New pipe-delimited format supporting complex scenarios:

```
TableName|SourceStrategy|where:condition -truncate
```

**Examples:**
```
CUSTTABLE|5000                              # Top 5000 by RecId
SALESLINE|days:30                           # Last 30 days
INVENTTRANS|all                             # Full table copy
CUSTTRANS|5000|where:DATAAREAID='1000'      # Top 5000 filtered by company
VENDTABLE|days:14|where:POSTED=1 -truncate  # Last 14 days, posted only, truncate first
```

### User Interface
- **Configuration Management**: Save and load multiple connection configurations
- **System Excluded Tables**: Separate management for system-level exclusions (SQL*, Sys*, Batch*, etc.)
- **Real-time Progress**: Monitor fetch/insert progress for each table
- **Sortable Grid**: Click column headers to sort
- **Context Menu**: Right-click to copy table names or generate SQL for testing
- **Get SQL Feature**: Preview all SQL operations (source query, cleanup, insert, sequence) without execution
- **Detailed Logging**: All SQL operations logged with timestamps
- **Menu System**: File menu (Save, Load, Exit) and Help menu (About)

### Technical Features
- Automatic sequence updates for D365FO tables
- Trigger management (disable during insert, re-enable after)
- Bulk insert with SqlBulkCopy for performance
- Transaction-based operations with rollback on errors
- Connection pooling for optimal performance
- Context-aware cleanup rules based on strategy type

## Requirements

- Windows OS
- .NET 9.0 Runtime
- SQL Server 2019+ (for local AxDB)
- Access to D365FO Azure SQL Database (Tier2)

## Configuration

### Connection Settings
- **Tier2 (Azure SQL)**: Server, database, credentials, timeouts
- **AxDB (Local SQL)**: Server, database, credentials, timeouts
- **System Excluded Tables**: System-level table exclusions (managed separately from user exclusions)
- **Execution**: Parallel fetch/insert connection counts

### Table Selection
- **Tables to Copy**: Patterns like `*`, `CUST*`, `SALES*` (one per line)
- **Tables to Exclude**: User-defined exclusions (default: `*Staging`)
- **System Excluded Tables**: System-level exclusions (default: `SQL*`, `UserInfo`, `Sys*`, `Batch*`, `RetailCDX*`, `RETAILHARDWAREPROFILE`)
- **Fields to Exclude**: Fields to skip (e.g., `SYSROWVERSION`)

### Copy Strategies

**Default**: RecId strategy with configurable record count

**Extended Per-Table Overrides**:
```
CUSTTABLE|5000                              # RecId: Top 5000 records
SALESLINE|days:30                           # ModifiedDate: Last 30 days
INVENTTRANS|all                             # Full table copy (auto-truncates)
CUSTTRANS|where:DATAAREAID='1000'           # WHERE only: All matching records
LEDGERTRANS|5000|where:DATAAREAID='1000'    # RecId + WHERE: Top 5000 filtered
SALESTABLE|days:30|where:STATUS=3           # ModifiedDate + WHERE: Last 30 days filtered
VENDTABLE|5000 -truncate                    # Force truncate before insert
```

**Cleanup Rules**:
- RecId only: Deletes records with RecId >= minimum fetched
- Days only: Deletes by ModifiedDateTime + fetched RecId list
- WHERE only: Deletes matching WHERE + RecId >= minimum
- RecId + WHERE: Deletes matching WHERE + RecId >= minimum
- Days + WHERE: Deletes by ModifiedDateTime + WHERE + RecId list
- ALL or -truncate: Truncates entire table

## Usage

1. **Configure Connections**: Set up Tier2 and AxDB connection details
2. **Configure Exclusions**: Set System Excluded Tables (Connection tab) and user exclusions (Tables tab)
3. **Select Tables**: Define inclusion/exclusion patterns
4. **Define Strategies**: Specify per-table copy strategies with optional WHERE clauses
5. **Discover Tables**: Discovers tables and validates schemas
6. **Fetch Data**: Fetches data from Tier2 in parallel
7. **Insert Data**: Inserts fetched data into AxDB in parallel

Or use **Run All** to execute all steps sequentially.

### Get SQL Feature

Right-click on any table in the grid after "Discover Tables" to generate formatted SQL showing:
- Source query (Tier2)
- Cleanup queries (AxDB) - step by step
- Insert operation details
- Sequence update SQL

This allows you to test and verify SQL logic without running the actual operations.

## Author

**Denis Trunin**

- GitHub: https://github.com/TrudAX/
- Copyright © 2025 Denis Trunin

## License

MIT License - See LICENSE file for details

## Notes

- Configuration files are stored in `configs/` directory (excluded from git)
- Always test with non-production data first
- Ensure proper database permissions before running
- Large tables may take significant time to copy
- WHERE clauses are passed directly to SQL - ensure they are properly formatted
- System Excluded Tables are combined with user-defined exclusions during execution
