using System.Text;
using DBSyncTool.Models;
using DBSyncTool.Services;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// Builds the "Get SQL" preview: the operations a table will go through, without running
    /// them. Mirrors CopyOrchestrator.ProcessSingleTableAsync / ProcessTableOptimizedAsync /
    /// ProcessTableStandardModeAsync and AxDbDataService.InsertDataAsync - keep in sync.
    /// </summary>
    public static class SqlPreviewBuilder
    {
        /// <summary>
        /// Route a table takes in CopyOrchestrator.ProcessSingleTableAsync.
        /// </summary>
        public enum PreviewRoute { System, Standard, Optimized }

        private static bool HasCopyableField(TableInfo table, string field)
        {
            return table.CopyableFields.Any(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasRowVersionPlaceholder(TableInfo table)
        {
            return !string.IsNullOrWhiteSpace(table.SqlTemplate) &&
                   table.SqlTemplate.Contains("@sysRowVersionFilter", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Mirrors the routing in CopyOrchestrator.ProcessSingleTableAsync (System → UseTruncate
        /// → UseOptimizedMode → standard) plus the SQL-without-placeholder fallback inside
        /// ProcessTableOptimizedAsync. Keep in sync with those methods.
        /// </summary>
        public static PreviewRoute GetPreviewRoute(TableInfo table, out string reason)
        {
            if (table.StrategyType == CopyStrategyType.System)
            {
                reason = "table is listed on the System tab";
                return PreviewRoute.System;
            }

            if (table.UseTruncate)
            {
                reason = "-truncate flag or 'Force truncate mode' is set - SysRowVersion optimization is skipped";
                return PreviewRoute.Standard;
            }

            // -notruncate only cancels the global 'Force truncate mode'; the routing below is
            // the normal one, including the automatic TRUNCATE once the threshold is exceeded.
            string noTruncateNote = table.NoTruncate
                ? "; -notruncate: 'Force truncate mode' does not apply to this table"
                : "";

            if (table.UseOptimizedMode)
            {
                if (table.StrategyType == CopyStrategyType.Sql && !HasRowVersionPlaceholder(table))
                {
                    reason = "SQL strategy has no @sysRowVersionFilter placeholder - optimized mode falls back to standard at runtime" + noTruncateNote;
                    return PreviewRoute.Standard;
                }
                reason = "table has SysRowVersion and both timestamps are stored" + noTruncateNote;
                return PreviewRoute.Optimized;
            }

            reason = (HasCopyableField(table, "SYSROWVERSION")
                ? "no saved values yet - timestamps are stored after this run, enabling optimized mode next time"
                : "table has no SysRowVersion column") + noTruncateNote;
            return PreviewRoute.Standard;
        }

        public static string GenerateSqlForTable(TableInfo table, AppConfiguration config)
        {
            var route = GetPreviewRoute(table, out string reason);
            return route switch
            {
                PreviewRoute.System => GenerateSqlForSystemTable(table, reason, config),
                PreviewRoute.Optimized => GenerateSqlForTableOptimized(table, reason, config),
                _ => GenerateSqlForTableStandard(table, reason, config)
            };
        }

        private static void AppendPreviewHeader(System.Text.StringBuilder sql, TableInfo table, string mode, string reason, AppConfiguration config, bool savedTimestampsUsed)
        {
            bool isSystem = table.StrategyType == CopyStrategyType.System;
            int recordCount = table.RecIdCount ?? config.DefaultRecordCount;

            sql.AppendLine("-- ==========================================================================");
            sql.AppendLine($"-- Table:    {table.TableName}");
            sql.AppendLine($"-- Strategy: {DescribeStrategy(table, recordCount, config)}");
            sql.AppendLine($"-- Mode:     {mode}");
            sql.AppendLine($"--           {reason}");
            sql.AppendLine("--");
            sql.AppendLine($"-- Tier2 rows: {table.Tier2RowCount:N0}   Est size: {table.EstimatedSizeMB:N1} MB");

            if (!isSystem)
            {
                sql.AppendLine($"-- TRUNCATE threshold: {config.TruncateThresholdPercent}%   " +
                               $"SysRowVersion: {(HasCopyableField(table, "SYSROWVERSION") ? "yes" : "no")}   " +
                               $"RECVERSION: {(HasCopyableField(table, "RECVERSION") ? "yes" : "no")}");
                // Saved timestamps are only used to route into optimized mode. In standard mode
                // they are ignored for this run but still overwritten at the end of it.
                string savedNote = savedTimestampsUsed ? "" : "   <- present but NOT used in this mode";
                sql.AppendLine($"-- Saved Tier2 timestamp: {(table.StoredTier2Timestamp != null ? TimestampHelper.ToHexString(table.StoredTier2Timestamp) + savedNote : "(none)")}");
                sql.AppendLine($"-- Saved AxDB timestamp:  {(table.StoredAxDBTimestamp != null ? TimestampHelper.ToHexString(table.StoredAxDBTimestamp) + savedNote : "(none)")}");
                sql.AppendLine($"-- Saved MaxRecId:        {(table.StoredMaxRecId.HasValue ? table.StoredMaxRecId.Value.ToString("N0") : "(none)")}");
                sql.AppendLine($"-- AxDB TableId: {table.AxDbTableId}   Sequence: SEQ_{table.AxDbTableId}");
            }

            sql.AppendLine("-- ==========================================================================");
            sql.AppendLine();
        }

        /// <summary>
        /// Human-readable strategy line: what will actually be fetched.
        /// </summary>
        private static string DescribeStrategy(TableInfo table, int recordCount, AppConfiguration config)
        {
            string truncateFlag = table.UseTruncate
                ? "  -truncate (forced TRUNCATE)"
                : table.NoTruncate ? "  -notruncate (exempt from 'Force truncate mode')" : "";

            switch (table.StrategyType)
            {
                case CopyStrategyType.System:
                    return "System - whole table" + (table.UseTruncate ? "  (always TRUNCATE + insert)" : "");

                case CopyStrategyType.Sql:
                    string counted = table.RecIdCount.HasValue
                        ? $", @recordCount = {recordCount:N0}"
                        : $", @recordCount = {recordCount:N0} (default)";
                    string placeholder = HasRowVersionPlaceholder(table)
                        ? ", has @sysRowVersionFilter"
                        : ", no @sysRowVersionFilter (optimized mode not possible)";
                    return $"SQL - custom query{counted}{placeholder}{truncateFlag}";

                default:
                    string source = table.RecIdCount.HasValue ? "" : " (default 'Records to copy')";
                    return $"RecId - top {recordCount:N0} rows by RecId DESC{source}{truncateFlag}";
            }
        }

        /// <summary>
        /// Sequence update as performed by UpdateSequenceAsync (both services).
        /// </summary>
        private static void AppendSequenceUpdate(System.Text.StringBuilder sql, TableInfo table, string stepTitle)
        {
            sql.AppendLine(stepTitle);
            sql.AppendLine($"SELECT MAX(RecId) FROM [{table.TableName}]   -- @MaxRecId; NULL (empty table) → nothing below runs");
            sql.AppendLine($"SELECT CAST(current_value AS BIGINT) FROM sys.sequences WHERE name = 'SEQ_{table.AxDbTableId}'");
            sql.AppendLine("-- @CurrentSeq; sequence not found → nothing below runs");
            sql.AppendLine($"ALTER SEQUENCE [SEQ_{table.AxDbTableId}] RESTART WITH <MAX(@MaxRecId, @CurrentSeq) + {AxDbDataService.SEQUENCE_GAP}>");
            sql.AppendLine("-- Always restarted (not only when MaxRecId is higher); the gap avoids RecId conflicts");
        }

        private static string GenerateSqlForSystemTable(TableInfo table, string reason, AppConfiguration config)
        {
            var sql = new System.Text.StringBuilder();

            AppendPreviewHeader(sql, table, "SYSTEM - full TRUNCATE + insert, copy strategy and saved values ignored", reason, config, false);

            sql.AppendLine("-- === STEP 1: FETCH FULL TABLE (Tier2) ===");
            sql.AppendLine(string.IsNullOrEmpty(table.FetchSql)
                ? $"SELECT * FROM [{table.TableName}]"
                : table.FetchSql);
            sql.AppendLine();

            sql.AppendLine("-- === STEP 2: TRUNCATE + INSERT (AxDB, no transaction) ===");
            sql.AppendLine($"ALTER TABLE [{table.TableName}] DISABLE TRIGGER ALL");
            sql.AppendLine($"TRUNCATE TABLE [{table.TableName}]                             -- falls back to DELETE FROM [{table.TableName}] if referenced by FK/view");
            sql.AppendLine("-- SqlBulkCopy: all fetched rows, batch size 10,000");
            sql.AppendLine($"ALTER TABLE [{table.TableName}] ENABLE TRIGGER ALL             -- always re-enabled, also on error");
            sql.AppendLine();

            sql.AppendLine("-- === NOT DONE FOR SYSTEM TABLES ===");
            sql.AppendLine("-- No delta comparison, no RecId/sequence update, no saved values written");

            return sql.ToString();
        }

        private static string GenerateSqlForTableStandard(TableInfo table, string reason, AppConfiguration config)
        {
            var sql = new System.Text.StringBuilder();
            bool hasSysRowVersion = HasCopyableField(table, "SYSROWVERSION");
            bool hasRecVersion = HasCopyableField(table, "RECVERSION");
            bool truncateGiven = table.UseTruncate;

            AppendPreviewHeader(sql, table, "STANDARD - fetch, then compare or truncate", reason, config, false);

            // Step 1: fetch
            sql.AppendLine("-- === STEP 1: FETCH FROM TIER2 ===");
            if (table.StrategyType == CopyStrategyType.Sql && HasRowVersionPlaceholder(table))
            {
                sql.AppendLine("-- @sysRowVersionFilter is replaced with (1 = 1) here: the full strategy is fetched");
            }
            sql.AppendLine(table.FetchSql);
            sql.AppendLine("-- MinRecId = MIN(RecId) of the fetched rows; RecordsToCopy becomes the fetched row count");
            sql.AppendLine();

            // Step 2: smart truncate detection
            sql.AppendLine("-- === STEP 2: SMART TRUNCATE DETECTION (AxDB) ===");
            if (truncateGiven)
            {
                sql.AppendLine("-- Skipped: TRUNCATE mode is already forced for this table");
            }
            else if (!hasSysRowVersion)
            {
                sql.AppendLine("-- Skipped: table has no SysRowVersion column");
            }
            else
            {
                sql.AppendLine($"SELECT COUNT(*) FROM [{table.TableName}]                       -- AxDB total rows");
                sql.AppendLine("-- excess% = (AxDB_total - fetched) / fetched * 100");
                sql.AppendLine($"-- excess% > {config.TruncateThresholdPercent}% → TRUNCATE mode is switched on for this run (branch A below)");
            }
            sql.AppendLine();

            // Step 3: cleanup + insert
            sql.AppendLine("-- === STEP 3: CLEANUP + INSERT (AxDB, no transaction) ===");
            sql.AppendLine($"ALTER TABLE [{table.TableName}] DISABLE TRIGGER ALL");
            sql.AppendLine();

            bool truncateOptimization = table.Tier2RowCount > 0 &&
                                        table.RecordsToCopy > 0 &&
                                        table.Tier2RowCount <= table.RecordsToCopy;

            if (truncateGiven)
            {
                sql.AppendLine("-- Branch A: TRUNCATE (forced) - no delta comparison");
                sql.AppendLine($"TRUNCATE TABLE [{table.TableName}]                             -- falls back to DELETE FROM if referenced by FK/view");
                sql.AppendLine("-- SqlBulkCopy: all fetched rows");
                sql.AppendLine("-- If Tier2 returned 0 rows the AxDB table is truncated and nothing is inserted");
            }
            else if (!hasRecVersion)
            {
                sql.AppendLine("-- Branch C: no RECVERSION column → no delta comparison, full cleanup by strategy");
                if (truncateOptimization)
                {
                    sql.AppendLine($"-- Tier2 rows ({table.Tier2RowCount:N0}) <= records to copy ({table.RecordsToCopy:N0}): whole table is copied");
                    sql.AppendLine($"TRUNCATE TABLE [{table.TableName}]                             -- falls back to DELETE FROM if referenced by FK/view");
                }
                else
                {
                    sql.AppendLine($"DELETE FROM [{table.TableName}] WHERE RecId >= @MinRecId");
                }
                sql.AppendLine("-- SqlBulkCopy: all fetched rows");
            }
            else
            {
                sql.AppendLine("-- Branch B: DELTA COMPARISON (RECVERSION present in Tier2 and AxDB)");
                sql.AppendLine($"SELECT RecId, RECVERSION{(HasCopyableField(table, "CREATEDDATETIME") ? ", CREATEDDATETIME" : "")}{(HasCopyableField(table, "MODIFIEDDATETIME") ? ", MODIFIEDDATETIME" : "")} FROM [{table.TableName}] WHERE RecId >= @MinRecId");
                sql.AppendLine("-- Each fetched row is classified as unchanged / modified / new; AxDB rows missing from");
                if (HasCopyableField(table, "CREATEDDATETIME") || HasCopyableField(table, "MODIFIEDDATETIME"))
                {
                    sql.AppendLine("-- the fetched set are 'deleted'. RECVERSION is the primary field, the datetime columns");
                    sql.AppendLine("-- above are tie-breakers.");
                }
                else
                {
                    sql.AppendLine("-- the fetched set are 'deleted'. Fallback mode: RECVERSION is the only comparison field,");
                    sql.AppendLine("-- so a RECVERSION=1 row counts as unchanged only when RecId <= saved MaxRecId");
                    sql.AppendLine($"-- ({(table.StoredMaxRecId.HasValue ? table.StoredMaxRecId.Value.ToString("N0") : "none saved yet - every RECVERSION=1 row is treated as modified")})");
                }
                sql.AppendLine();
                sql.AppendLine("--   B1: nothing modified/new/deleted → no DELETE, no INSERT (sequence update still runs)");
                sql.AppendLine($"--   B2: changed% >= {config.TruncateThresholdPercent}% → falls back to branch A cleanup + insert of all fetched rows");
                sql.AppendLine("--   B3: otherwise the delta below");
                sql.AppendLine();
                if (truncateOptimization)
                {
                    sql.AppendLine($"DELETE FROM [{table.TableName}] WHERE RecId < @MinRecId          -- whole table copied: drop rows below the fetched range");
                }
                sql.AppendLine($"DELETE FROM [{table.TableName}] WHERE RecId IN (...)             -- modified + deleted RecIds, batches of 5,000");
                sql.AppendLine("-- SqlBulkCopy: modified + new rows only");
            }

            sql.AppendLine();
            sql.AppendLine($"ALTER TABLE [{table.TableName}] ENABLE TRIGGER ALL             -- always re-enabled, also on error");
            sql.AppendLine();

            AppendSequenceUpdate(sql, table, "-- === STEP 4: SEQUENCE UPDATE (AxDB) ===");
            sql.AppendLine();

            sql.AppendLine("-- === STEP 5: SAVED VALUES ===");
            if (hasSysRowVersion)
            {
                sql.AppendLine($"SELECT MAX(SysRowVersion) FROM [{table.TableName}]             -- AxDB timestamp");
                sql.AppendLine("-- Tier2 timestamp = MAX(SysRowVersion) of the fetched rows");
                sql.AppendLine("-- Both are saved to the config, so the next run uses OPTIMIZED mode");
            }
            else
            {
                sql.AppendLine("-- No SysRowVersion: MAX(RecId) of the fetched rows is saved as MaxRecId");
                sql.AppendLine("-- (used by the delta comparison fallback above, not to limit the fetch)");
            }

            return sql.ToString();
        }

        private static string GenerateSqlForTableOptimized(TableInfo table, string reason, AppConfiguration config)
        {
            var sql = new System.Text.StringBuilder();
            int recordCount = table.RecIdCount ?? config.DefaultRecordCount;
            string fieldList = string.Join(", ", table.CopyableFields.Select(f => $"[{f}]"));
            string tier2TsHex = table.StoredTier2Timestamp != null
                ? TimestampHelper.ToHexString(table.StoredTier2Timestamp) : "N/A";
            string axdbTsHex = table.StoredAxDBTimestamp != null
                ? TimestampHelper.ToHexString(table.StoredAxDBTimestamp) : "N/A";
            bool sqlWithPlaceholder = table.StrategyType == CopyStrategyType.Sql && HasRowVersionPlaceholder(table);

            AppendPreviewHeader(sql, table, "OPTIMIZED - control query first, then TRUNCATE or INCREMENTAL", reason, config, true);

            // Step 1: control query
            sql.AppendLine("-- === STEP 1: CONTROL QUERY (Tier2) ===");
            sql.AppendLine("-- RecId + SysRowVersion only (~1 KB per 1000 rows instead of the full rows)");
            if (sqlWithPlaceholder)
            {
                sql.AppendLine(table.SqlTemplate
                    .Replace("*", "RecId, SysRowVersion", StringComparison.OrdinalIgnoreCase)
                    .Replace("@recordCount", recordCount.ToString())
                    .Replace("@sysRowVersionFilter", "(1 = 1)", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                sql.AppendLine($"SELECT TOP ({recordCount}) RecId, SysRowVersion FROM [{table.TableName}] ORDER BY RecId DESC");
            }
            sql.AppendLine("-- 0 rows returned → table is skipped and marked Inserted");
            sql.AppendLine();

            // Step 2: decision
            sql.AppendLine("-- === STEP 2: CHANGE EVALUATION ===");
            sql.AppendLine($"-- Tier2Changed = control rows with SysRowVersion > {tier2TsHex}   (in memory)");
            sql.AppendLine($"SELECT COUNT(*) FROM [{table.TableName}] WHERE SysRowVersion > {axdbTsHex}   -- AxDBChanged");
            sql.AppendLine($"SELECT COUNT(*) FROM [{table.TableName}]                                       -- AxDBTotal");
            sql.AppendLine("--");
            sql.AppendLine("-- change% = (Tier2Changed + AxDBChanged) / controlRows * 100");
            sql.AppendLine("-- excess% = (AxDBTotal - controlRows) / controlRows * 100");
            sql.AppendLine($"-- change% >= {config.TruncateThresholdPercent}% OR excess% >= {config.TruncateThresholdPercent}%  → BRANCH A (TRUNCATE)");
            sql.AppendLine("-- otherwise                                → BRANCH B (INCREMENTAL)");
            sql.AppendLine();

            // Branch A
            sql.AppendLine("-- === BRANCH A: TRUNCATE MODE ===");
            sql.AppendLine("-- Full fetch with the copy strategy (@sysRowVersionFilter → (1 = 1)):");
            sql.AppendLine(table.FetchSql);
            sql.AppendLine($"ALTER TABLE [{table.TableName}] DISABLE TRIGGER ALL");
            sql.AppendLine($"TRUNCATE TABLE [{table.TableName}]                             -- falls back to DELETE FROM if referenced by FK/view");
            sql.AppendLine("-- SqlBulkCopy: all fetched rows");
            sql.AppendLine($"ALTER TABLE [{table.TableName}] ENABLE TRIGGER ALL");
            sql.AppendLine("-- Then: sequence update + saved values (see bottom)");
            sql.AppendLine();

            // Branch B
            sql.AppendLine("-- === BRANCH B: INCREMENTAL MODE (single transaction) ===");
            sql.AppendLine("-- B0 fast path - Tier2Changed = 0 AND AxDBChanged = 0 AND excess% = 0:");
            sql.AppendLine($"SELECT RecId FROM [{table.TableName}]                          -- RecIds already in AxDB");
            sql.AppendLine("--     no missing RecIds  → nothing at all is executed, only saved values are refreshed");
            sql.AppendLine("--     missing RecIds     → skip B1-B3, insert those rows only (B4 onwards)");
            sql.AppendLine();
            sql.AppendLine($"ALTER TABLE [{table.TableName}] DISABLE TRIGGER ALL");
            sql.AppendLine();
            sql.AppendLine("-- B1: load the control rows into a temp table");
            sql.AppendLine("CREATE TABLE #Tier2Control (RecId BIGINT PRIMARY KEY, SysRowVersion BINARY(8))");
            sql.AppendLine("-- SqlBulkCopy: the control rows from step 1");
            sql.AppendLine();
            sql.AppendLine("-- B2: three deletes");
            sql.AppendLine($"DELETE FROM [{table.TableName}]                                -- changed in Tier2");
            sql.AppendLine(" WHERE RecId IN (SELECT RecId FROM #Tier2Control WHERE SysRowVersion > @Tier2Timestamp)");
            sql.AppendLine($"DELETE FROM [{table.TableName}]                                -- changed locally in AxDB");
            sql.AppendLine(" WHERE SysRowVersion > @AxDBTimestamp");
            sql.AppendLine($"DELETE FROM [{table.TableName}]                                -- no longer in the Tier2 target set");
            sql.AppendLine($" WHERE NOT EXISTS (SELECT 1 FROM #Tier2Control t WHERE t.RecId = [{table.TableName}].RecId)");
            sql.AppendLine("DROP TABLE #Tier2Control");
            sql.AppendLine($"-- @Tier2Timestamp = {tier2TsHex}");
            sql.AppendLine($"-- @AxDBTimestamp  = {axdbTsHex}");
            sql.AppendLine();
            sql.AppendLine("-- B3: which RecIds are missing now");
            sql.AppendLine($"SELECT RecId FROM [{table.TableName}]");
            sql.AppendLine("-- missing = control RecIds not present in AxDB; none → nothing is inserted");
            sql.AppendLine();
            sql.AppendLine("-- B4: fetch the missing rows from Tier2");
            if (sqlWithPlaceholder)
            {
                sql.AppendLine(table.SqlTemplate
                    .Replace("*", fieldList, StringComparison.OrdinalIgnoreCase)
                    .Replace("@recordCount", recordCount.ToString())
                    .Replace("@sysRowVersionFilter", "SysRowVersion >= @Threshold AND RecId >= @MinRecId", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                sql.AppendLine($"SELECT TOP ({recordCount}) {fieldList} FROM [{table.TableName}]");
                sql.AppendLine(" WHERE SysRowVersion >= @Threshold AND RecId >= @MinRecId");
                sql.AppendLine(" ORDER BY RecId DESC");
            }
            sql.AppendLine($"-- @Threshold = MIN(smallest SysRowVersion of the missing RecIds, {tier2TsHex})");
            sql.AppendLine("-- @MinRecId  = MIN(RecId) over ALL control rows");
            sql.AppendLine("-- Rows whose RecId still exists in AxDB are dropped client-side");
            sql.AppendLine();
            sql.AppendLine("-- B5: SqlBulkCopy the remaining rows, then");
            sql.AppendLine($"ALTER TABLE [{table.TableName}] ENABLE TRIGGER ALL");
            sql.AppendLine("-- sequence update (below), COMMIT");
            sql.AppendLine();

            AppendSequenceUpdate(sql, table, "-- === SEQUENCE UPDATE (both branches) ===");
            sql.AppendLine();

            sql.AppendLine("-- === SAVED VALUES (both branches) ===");
            sql.AppendLine($"SELECT MAX(SysRowVersion) FROM [{table.TableName}]             -- new AxDB timestamp");
            sql.AppendLine("-- New Tier2 timestamp = MAX(SysRowVersion) of the control rows");

            return sql.ToString();
        }    }
}
