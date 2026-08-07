using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal sealed class EntraTokenProvider
{
    private readonly TokenCredential _credential;
    private readonly EntraSiloConnectionOptions _options;
    private readonly TimeProvider _timeProvider;

    public EntraTokenProvider(TokenCredential credential, EntraSiloConnectionOptions options, TimeProvider timeProvider)
    {
        _credential = credential;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext([_options.TokenScope!]),
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(token.Token)
            || token.ExpiresOn - _timeProvider.GetUtcNow() < _options.MinimumRemainingTokenLifetime)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.TokenAcquisitionFailed);
        }

        return token;
    }
}
