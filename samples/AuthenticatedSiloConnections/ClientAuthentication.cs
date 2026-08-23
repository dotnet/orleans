using Azure.Core;
using Orleans.Hosting;

namespace AuthenticatedSiloConnections;

internal static class ClientAuthentication
{
    public static void Configure(
        IClientBuilder clientBuilder,
        SampleOptions options,
        TokenCredential credential)
    {
        // <AuthenticatedClient>
        clientBuilder.UseAuthenticatedClientConnections(
            tls =>
            {
                tls.CheckCertificateRevocation = true;
            },
            authentication =>
            {
                SiloAuthentication.ConfigureAuthentication(
                    authentication,
                    options,
                    credential,
                    options.Entra.AllowedClientCallerClientIds,
                    options.Entra.ClientClusterRole);
            });
        // </AuthenticatedClient>
    }
}
