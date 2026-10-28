using DBSyncTool.Models;

namespace DBSyncTool.Helpers
{
    public enum StrategyChangeKind
    {
        Added,
        Modified,
        Removed
    }

    /// <summary>
    /// A single Copy strategy line that was added, modified or removed by the user.
    /// </summary>
    public class StrategyChange
    {
        public string TableName { get; init; } = string.Empty;
        public StrategyChangeKind Kind { get; init; }

        /// <summary>
        /// True when the edit can widen the fetch (bigger record count, any SQL change) and
        /// saved values would therefore hide it. False only when the new strategy provably
        /// fetches no more data than before - e.g. a lowered record count - in which case the
        /// saved values stay valid and are worth keeping.
        /// </summary>
        public bool MayFetchMoreData { get; init; }
    }

    /// <summary>
    /// Saved values (SysRowVersion timestamps + MaxRecId) removed for one table.
    /// </summary>
    public class SavedValuesRemoval
    {
        public string TableName { get; init; } = string.Empty;
        public bool Tier2Timestamp { get; init; }
        public bool AxDbTimestamp { get; init; }
        public bool MaxRecId { get; init; }

        public string Describe()
        {
            var parts = new List<string>();
            if (Tier2Timestamp) parts.Add("Tier2 timestamp");
            if (AxDbTimestamp) parts.Add("AxDB timestamp");
            if (MaxRecId) parts.Add("MaxRecId");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Detects Copy strategy edits and removes the stored optimization values for the
    /// affected tables. Without this, a changed strategy (for example a larger record
    /// count) would not be applied on the next run: the stored timestamps keep the table
    /// in INCREMENTAL mode, which fetches only rows newer than the saved timestamp.
    /// </summary>
    public static class SavedValuesHelper
    {
        /// <summary>
        /// Table name of a strategy line: "CUSTTABLE|5000 -truncate" -> "CUSTTABLE"
        /// </summary>
        public static string GetStrategyTableName(string line)
        {
            return StripFlags(line, out _, out _).Split('|')[0].Trim();
        }

        /// <summary>
        /// Removes the trailing -truncate / -notruncate flags from a strategy line, mirroring
        /// CopyOrchestrator.ParseStrategyLine.
        /// </summary>
        private static string StripFlags(string line, out bool useTruncate, out bool noTruncate)
        {
            useTruncate = false;
            noTruncate = false;

            var l = line.Trim();
            while (true)
            {
                if (l.EndsWith(" -truncate", StringComparison.OrdinalIgnoreCase))
                {
                    useTruncate = true;
                    l = l.Substring(0, l.Length - " -truncate".Length).Trim();
                }
                else if (l.EndsWith(" -notruncate", StringComparison.OrdinalIgnoreCase))
                {
                    noTruncate = true;
                    l = l.Substring(0, l.Length - " -notruncate".Length).Trim();
                }
                else
                {
                    return l;
                }
            }
        }

        /// <summary>
        /// Compares two Copy strategy texts and returns the tables whose strategy line was
        /// added, modified or removed. Order-independent, so sorting the list reports nothing.
        /// A table without a line uses the default record count, which is why added and
        /// removed lines are compared against it.
        /// </summary>
        public static List<StrategyChange> GetStrategyChanges(string? oldText, string? newText,
            int oldDefaultRecordCount, int newDefaultRecordCount)
        {
            var oldLines = ParseStrategyLines(oldText);
            var newLines = ParseStrategyLines(newText);
            var changes = new List<StrategyChange>();

            foreach (var kvp in newLines)
            {
                if (!oldLines.TryGetValue(kvp.Key, out var oldLine))
                {
                    changes.Add(new StrategyChange
                    {
                        TableName = kvp.Key,
                        Kind = StrategyChangeKind.Added,
                        MayFetchMoreData = MayFetchMoreData(null, oldDefaultRecordCount, kvp.Value, newDefaultRecordCount)
                    });
                }
                else if (!string.Equals(oldLine, kvp.Value, StringComparison.Ordinal))
                {
                    changes.Add(new StrategyChange
                    {
                        TableName = kvp.Key,
                        Kind = StrategyChangeKind.Modified,
                        MayFetchMoreData = MayFetchMoreData(oldLine, oldDefaultRecordCount, kvp.Value, newDefaultRecordCount)
                    });
                }
            }

            foreach (var kvp in oldLines)
            {
                if (!newLines.ContainsKey(kvp.Key))
                {
                    changes.Add(new StrategyChange
                    {
                        TableName = kvp.Key,
                        Kind = StrategyChangeKind.Removed,
                        MayFetchMoreData = MayFetchMoreData(kvp.Value, oldDefaultRecordCount, null, newDefaultRecordCount)
                    });
                }
            }

            return changes.OrderBy(c => c.TableName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Can the new strategy fetch data the old one did not? Only a plain RecId count that
        /// stays the same or gets smaller is provably safe - anything involving SQL is treated
        /// as widening, since a changed WHERE clause cannot be compared.
        /// </summary>
        private static bool MayFetchMoreData(string? oldLine, int oldDefaultCount, string? newLine, int newDefaultCount)
        {
            var oldStrategy = ParseStrategy(oldLine, oldDefaultCount);
            var newStrategy = ParseStrategy(newLine, newDefaultCount);

            // Unparseable or SQL on either side: cannot compare, assume more data
            if (!oldStrategy.Valid || !newStrategy.Valid || oldStrategy.IsSql || newStrategy.IsSql)
                return true;

            // TRUNCATE forces a full refresh from the strategy, so saved values cannot hide it
            if (newStrategy.UseTruncate)
                return false;

            return newStrategy.RecordCount > oldStrategy.RecordCount;
        }

        private struct ParsedStrategy
        {
            public bool Valid;
            public bool IsSql;
            public bool UseTruncate;
            public long RecordCount;
        }

        /// <summary>
        /// Minimal strategy parse mirroring CopyOrchestrator.ParseStrategyLine, used only to
        /// compare how much data two strategy lines fetch. A null line means "no line", i.e.
        /// the default record count. Invalid lines are reported as not valid, never thrown.
        /// </summary>
        private static ParsedStrategy ParseStrategy(string? line, int defaultRecordCount)
        {
            if (line == null)
                return new ParsedStrategy { Valid = true, RecordCount = defaultRecordCount };

            var working = StripFlags(line, out bool useTruncate, out bool noTruncate);
            if (useTruncate && noTruncate)
                return new ParsedStrategy { Valid = false };

            var parts = working.Split('|');
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                return new ParsedStrategy { Valid = false };

            // TableName only -> default count
            if (parts.Length == 1)
                return new ParsedStrategy { Valid = true, UseTruncate = useTruncate, RecordCount = defaultRecordCount };

            var part1 = parts[1].Trim();
            if (part1.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
                return new ParsedStrategy { Valid = true, IsSql = true, UseTruncate = useTruncate };

            if (!TryParseRecordCount(part1, out long count) || count <= 0)
                return new ParsedStrategy { Valid = false };

            // TableName|Count|sql:...
            if (parts.Length >= 3 && parts[2].Trim().StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
                return new ParsedStrategy { Valid = true, IsSql = true, UseTruncate = useTruncate, RecordCount = count };

            if (parts.Length >= 3)
                return new ParsedStrategy { Valid = false };

            return new ParsedStrategy { Valid = true, UseTruncate = useTruncate, RecordCount = count };
        }

        /// <summary>
        /// Record count with optional 'm'/'M' suffix for millions (10m = 10,000,000).
        /// </summary>
        private static bool TryParseRecordCount(string input, out long count)
        {
            if (input.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(input.Substring(0, input.Length - 1), out long millions))
                {
                    count = millions * 1_000_000;
                    return true;
                }
                count = 0;
                return false;
            }
            return long.TryParse(input, out count);
        }

        /// <summary>
        /// Subset of the given tables that currently have a stored timestamp or MaxRecId.
        /// </summary>
        public static List<string> GetTablesWithSavedValues(AppConfiguration config, IEnumerable<string> tableNames)
        {
            var stored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddStoredTableNames(config.Tier2Timestamps, stored);
            AddStoredTableNames(config.AxDBTimestamps, stored);
            AddStoredTableNames(config.MaxTransferredRecIds, stored);

            return tableNames.Where(t => stored.Contains(t.Trim())).ToList();
        }

        private static void AddStoredTableNames(string? text, HashSet<string> target)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Split(',')[0].Trim();
                if (name.Length > 0) target.Add(name);
            }
        }

        /// <summary>
        /// Removes the stored Tier2/AxDB timestamps and MaxRecIds for the given tables from
        /// the configuration. Returns one entry per table that actually had a saved value.
        /// </summary>
        public static List<SavedValuesRemoval> RemoveSavedValues(AppConfiguration config, IEnumerable<string> tableNames)
        {
            var tables = new HashSet<string>(
                tableNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (tables.Count == 0) return new List<SavedValuesRemoval>();

            config.Tier2Timestamps = RemoveTableLines(config.Tier2Timestamps, tables, out var tier2Removed);
            config.AxDBTimestamps = RemoveTableLines(config.AxDBTimestamps, tables, out var axdbRemoved);
            config.MaxTransferredRecIds = RemoveTableLines(config.MaxTransferredRecIds, tables, out var maxRecIdRemoved);

            var affected = new HashSet<string>(tier2Removed, StringComparer.OrdinalIgnoreCase);
            affected.UnionWith(axdbRemoved);
            affected.UnionWith(maxRecIdRemoved);

            return affected
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(t => new SavedValuesRemoval
                {
                    TableName = t,
                    Tier2Timestamp = tier2Removed.Contains(t, StringComparer.OrdinalIgnoreCase),
                    AxDbTimestamp = axdbRemoved.Contains(t, StringComparer.OrdinalIgnoreCase),
                    MaxRecId = maxRecIdRemoved.Contains(t, StringComparer.OrdinalIgnoreCase)
                })
                .ToList();
        }

        /// <summary>
        /// Strategy lines keyed by table name (case-insensitive). Last line wins for duplicates.
        /// </summary>
        private static Dictionary<string, string> ParseStrategyLines(string? text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var tableName = GetStrategyTableName(trimmed);
                if (tableName.Length == 0) continue;

                result[tableName] = trimmed;
            }
            return result;
        }

        /// <summary>
        /// Drops "TABLENAME,value" lines whose table is in the set, keeping everything else as is.
        /// </summary>
        private static string RemoveTableLines(string? text, HashSet<string> tables, out List<string> removed)
        {
            removed = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;

            var kept = new List<string>();
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var tableName = trimmed.Split(',')[0].Trim();
                if (tables.Contains(tableName))
                {
                    removed.Add(tableName);
                }
                else
                {
                    kept.Add(trimmed);
                }
            }

            return string.Join("\r\n", kept);
        }
    }
}
