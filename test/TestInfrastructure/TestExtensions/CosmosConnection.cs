namespace TestExtensions
{
    public class CosmosConnection
    {
        public static CosmosConnection LocalCosmosEmulator = new CosmosConnection
        {
            // hardcoded for emulator https://learn.microsoft.com/en-us/azure/cosmos-db/emulator#authentication
            ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="
        };

        /// <summary>
        /// used for connectionString auth
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// used for RBAC / MSI auth
        /// </summary>
        public string AccountEndpoint { get; set; }
    }
}