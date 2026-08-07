using System;
using Azure.Core;

namespace Orleans.Connections.Security.Entra;

internal sealed record EntraCredentialRegistration(TokenCredential Credential);

internal sealed class EntraTimeProviderAccessor(Func<TimeProvider> getTimeProvider)
{
    public TimeProvider Value => getTimeProvider();
}
