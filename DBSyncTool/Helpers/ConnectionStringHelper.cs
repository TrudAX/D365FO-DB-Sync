namespace DBSyncTool.Helpers
{
    /// <summary>
    /// Helper class for parsing database connection strings.
    /// </summary>
    public static class ConnectionStringHelper
    {
        /// <summary>
        /// Parses a connection string and extracts key-value pairs.
        /// </summary>
        /// <param name="connectionString">The connection string to parse (e.g., "Server=...;Database=...;User Id=...;Password=...")</param>
        /// <returns>Dictionary containing parsed key-value pairs with case-insensitive keys</returns>
        public static Dictionary<string, string> ParseConnectionString(string connectionString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(connectionString))
                return result;

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
