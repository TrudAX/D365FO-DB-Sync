namespace DBSyncTool.Helpers
{
    /// <summary>
    /// Helper class for parsing database connection strings.
    /// </summary>
    public static class ConnectionStringHelper
    {
        /// <summary>
        /// Parses a connection string and extracts key-value pairs.
        /// Supports two formats:
        /// 1. Standard format: "Server=myserver.database.windows.net;Database=mydb;User Id=myuser;Password=mypass"
        /// 2. Three-line format (from LCS):
        ///    Line 1: myserver.database.windows.net\mydb
        ///    Line 2: myuser
        ///    Line 3: mypass
        /// </summary>
        /// <param name="connectionString">The connection string to parse</param>
        /// <returns>Dictionary containing parsed key-value pairs with case-insensitive keys</returns>
        public static Dictionary<string, string> ParseConnectionString(string connectionString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(connectionString))
                return result;

            // Check if it's a three-line format (contains newlines but no '=' signs)
            if (connectionString.Contains('\n') && !connectionString.Contains('='))
            {
                return ParseThreeLineFormat(connectionString);
            }

            // Standard key=value format
            // Split by semicolon and process each key=value pair
            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var trimmedPart = part.Trim();
                if (string.IsNullOrWhiteSpace(trimmedPart))
                    continue;

                var equalIndex = trimmedPart.IndexOf('=');
                if (equalIndex > 0)
                {
                    string key = trimmedPart.Substring(0, equalIndex).Trim();
                    string value = trimmedPart.Substring(equalIndex + 1).Trim();
                    result[key] = value;
                }
            }

            return result;
        }

        /// <summary>
        /// Parses a three-line connection string format (from LCS).
        /// Format:
        ///   Line 1: myserver.database.windows.net\mydb
        ///   Line 2: myuser
        ///   Line 3: mypass
        /// </summary>
        /// <param name="connectionString">The three-line connection string</param>
        /// <returns>Dictionary with Server, Database, User Id, and Password keys</returns>
        private static Dictionary<string, string> ParseThreeLineFormat(string connectionString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Split by newlines and remove empty entries
            var lines = connectionString.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(l => l.Trim())
                                       .Where(l => !string.IsNullOrWhiteSpace(l))
                                       .ToArray();

            if (lines.Length < 3)
            {
                // Not enough lines for three-line format
                return result;
            }

            // Line 1: Server\Database
            var serverDbLine = lines[0];
            var backslashIndex = serverDbLine.IndexOf('\\');
            if (backslashIndex > 0)
            {
                result["Server"] = serverDbLine.Substring(0, backslashIndex).Trim();
                result["Database"] = serverDbLine.Substring(backslashIndex + 1).Trim();
            }

            // Line 2: Username
            result["User Id"] = lines[1];

            // Line 3: Password
            result["Password"] = lines[2];

            return result;
        }

        /// <summary>
        /// Formats server and database into the Server\Database format used by the application.
        /// </summary>
        /// <param name="server">Database server address</param>
        /// <param name="database">Database name</param>
        /// <returns>Formatted string in "Server\Database" format</returns>
        public static string FormatServerDatabase(string server, string database)
        {
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
                return string.Empty;

            return $"{server}\\{database}";
        }
    }
}
