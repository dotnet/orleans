using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal sealed class EntraSiloConnectionTokenProvider : ISiloConnectionTokenProvider
{
    private readonly EntraTokenProvider _provider;

    public EntraSiloConnectionTokenProvider(
        IEnumerable<EntraCredentialRegistration> credentialRegistrations,
        IOptions<EntraSiloConnectionOptions> options,
        EntraTimeProviderAccessor timeProvider)
    {
        var registration = credentialRegistrations.Single();
        _provider = new EntraTokenProvider(registration.Credential, options.Value, timeProvider.Value);
    }

    public async ValueTask<SiloConnectionToken> GetTokenAsync(
        SiloConnectionTokenRequestContext context,
        CancellationToken cancellationToken)
    {
        var token = await _provider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return new SiloConnectionToken(token.Token, token.ExpiresOn);
    }
}
