using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Queues;

namespace DistributedTests.Common;

public static class TokenCredentialHelper
{
    public static TokenCredential GetTokenCredential()
    {
        var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
        if (tenantId != null && clientId != null)
        {
            // Uses Federated Id Creds, from here:
            // https://review.learn.microsoft.com/en-us/identity/microsoft-identity-platform/federated-identity-credentials?branch=main&tabs=dotnet#azure-sdk-for-net
            return new ClientAssertionCredential(
              tenantId, // Tenant ID for destination resource
              clientId,  // Client ID of the app we're federating to
              () => GetManagedIdentityToken(null, "api://AzureADTokenExchange")) // null here for default MSI
            ;
        }
        else
        {
            return new DefaultAzureCredential();
        }
    }

    /// <summary>
    /// Gets a token for the user-assigned Managed Identity.
    /// </summary>
    /// <param name="msiClientId">Client ID for the Managed Identity.</param>
    /// <param name="audience">Target audience. For public clouds should be api://AzureADTokenExchange.</param>
    /// <returns>If successful, returns an access token.</returns>
    public static string GetManagedIdentityToken(string msiClientId, string audience)
    {
        var miCredential = new ManagedIdentityCredential(msiClientId);
        return miCredential.GetToken(new TokenRequestContext(new[] { $"{audience}/.default" })).Token;
    }
}
