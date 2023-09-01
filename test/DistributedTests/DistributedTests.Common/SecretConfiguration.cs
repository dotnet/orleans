using Microsoft.Extensions.Configuration;

namespace DistributedTests
{
    public class SecretConfiguration
    {
        public enum SecretSource
        {
            File,
            KeyVault,
        }

        private readonly IConfiguration _configuration;

        public string ClusteringConnectionString => _configuration[nameof(ClusteringConnectionString)];

        public SecretConfiguration(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public static SecretConfiguration LoadFromJson(string filename = "secrets.json")
        {
            var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (currentDir != null && currentDir.Exists)
            {
                var filePath = Path.Combine(currentDir.FullName, filename);
                if (File.Exists(filePath))
                {
                    var config = new ConfigurationBuilder()
                        .AddJsonFile(filePath)
                        .Build();
                    return new SecretConfiguration(config);
                }
                currentDir = currentDir.Parent;
            }
            throw new FileNotFoundException("Cannot find the secret file", filename);
        }

        public static SecretConfiguration LoadFromKeyVault()
        {
            var vaultUri = Environment.GetEnvironmentVariable("KVP_URI") ?? throw new ArgumentException("KVP_URI environment variable not set");
            var clientId = Environment.GetEnvironmentVariable("KVP_CLIENTID") ?? throw new ArgumentException("KVP_CLIENTID environment variable not set");
            var clientSecret = Environment.GetEnvironmentVariable("KVP_SECRET") ?? throw new ArgumentException("KVP_SECRET environment variable not set");
            
            var config = new ConfigurationBuilder()
                .AddAzureKeyVault(vaultUri, clientId, clientSecret)
                .Build();

            return new SecretConfiguration(config);
        }

        public static SecretConfiguration Load(SecretSource source)
        {
            return source switch
            {
                SecretSource.File => LoadFromJson(),
                SecretSource.KeyVault => LoadFromKeyVault(),
                _ => throw new ArgumentException("Unsupported source", nameof(source)),
            };
        }
    }
}
