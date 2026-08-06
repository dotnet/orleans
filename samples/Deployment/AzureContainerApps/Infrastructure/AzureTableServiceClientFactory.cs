using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

public static class AzureTableServiceClientFactory
{
    public static TableServiceClient Create(IConfiguration configuration, IHostEnvironment environment)
    {
        var serviceUriValue = configuration["AzureTable:ServiceUri"];
        if (!string.IsNullOrWhiteSpace(serviceUriValue))
        {
            if (!Uri.TryCreate(serviceUriValue, UriKind.Absolute, out var serviceUri)
                || serviceUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    "AzureTable:ServiceUri must be an absolute HTTPS Azure Table service URI.");
            }

            var credentialOptions = new DefaultAzureCredentialOptions();
            var managedIdentityClientId = GetRequiredValue(configuration, "AZURE_CLIENT_ID");
            if (!Guid.TryParse(managedIdentityClientId, out _))
            {
                throw new InvalidOperationException(
                    "AZURE_CLIENT_ID must contain the user-assigned managed identity client ID.");
            }

            credentialOptions.ManagedIdentityClientId = managedIdentityClientId;
            return new TableServiceClient(serviceUri, new DefaultAzureCredential(credentialOptions));
        }

        var connectionString = configuration["AzureTable:ConnectionString"];
        if (environment.IsDevelopment()
            && string.Equals(connectionString, "UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
        {
            return new TableServiceClient(connectionString);
        }

        throw new InvalidOperationException(
            "Configure AzureTable:ServiceUri for Azure, or use Azurite with "
            + "AzureTable:ConnectionString=UseDevelopmentStorage=true in Development.");
    }

    public static string GetRequiredValue(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{key} is not configured.");
    }
}
