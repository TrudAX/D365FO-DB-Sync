namespace DBCopyTool.Models
{
    public class ConnectionSettings
    {
        public string ServerDatabase { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public int ConnectionTimeout { get; set; } = 3;
        public int CommandTimeout { get; set; } = 600;

        // Helper method to parse ServerDatabase
        public (string Server, string Database) ParseServerDatabase()
        {
            var parts = ServerDatabase?.Split('\\', 2) ?? Array.Empty<string>();
            return (
                parts.Length > 0 ? parts[0] : "",
                parts.Length > 1 ? parts[1] : ""
            );
        }

        // Helper to build connection string
        public string BuildConnectionString(bool isAzure = false)
        {
            var (server, database) = ParseServerDatabase();

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
                throw new InvalidOperationException("Server and Database must be specified");

            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                UserID = Username,
                Password = Password,
                Pooling = true,
                MaxPoolSize = 20,
                TrustServerCertificate = true  // Trust server certificate (Azure SQL and local SQL)
            };

            if (isAzure)
            {
                builder.ConnectTimeout = ConnectionTimeout;
                builder.Encrypt = true;
                builder.ApplicationIntent = Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly;  // Read-only hint for Azure SQL
            }

            return builder.ConnectionString;
        }
    }
}
