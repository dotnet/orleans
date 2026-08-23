using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal sealed class EntraSiloConnectionTokenProvider : ISiloConnectionTokenProvider
{
    private readonly IEntraTokenProvider _provider;

    public EntraSiloConnectionTokenProvider(
        Azure.Core.TokenCredential credential,
        EntraSiloConnectionOptions options,
        TimeProvider timeProvider)
    {
        _provider = new EntraTokenProvider(credential, options, timeProvider);
    }

    internal EntraSiloConnectionTokenProvider(IEntraTokenProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public async ValueTask<SiloConnectionToken> GetTokenAsync(
        SiloConnectionTokenRequestContext context,
        CancellationToken cancellationToken)
    {
        var token = await _provider.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        return new SiloConnectionToken(token.Token, token.ExpiresOn);
    }
}
