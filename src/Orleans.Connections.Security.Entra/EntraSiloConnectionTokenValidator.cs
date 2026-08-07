using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal sealed class EntraSiloConnectionTokenValidator : ISiloConnectionTokenValidator
{
    private readonly EntraJwtValidator _validator;

    public EntraSiloConnectionTokenValidator(EntraJwtValidator validator)
    {
        _validator = validator;
    }

    public async ValueTask<SiloConnectionTokenValidationResult> ValidateTokenAsync(
        string token,
        SiloConnectionTokenValidationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _validator.ValidateAsync(token, context.ClusterId, cancellationToken).ConfigureAwait(false);
            return SiloConnectionTokenValidationResult.Success(result.Principal, result.ExpiresAt);
        }
        catch (EntraAuthenticationException exception)
        {
            return SiloConnectionTokenValidationResult.Fail(MapFailure(exception.Error));
        }
    }

    private static SiloConnectionAuthenticationFailure MapFailure(EntraAuthenticationError error) => error switch
    {
        EntraAuthenticationError.ExpiredToken => SiloConnectionAuthenticationFailure.ExpiredToken,
        EntraAuthenticationError.UnauthorizedCaller => SiloConnectionAuthenticationFailure.UnauthorizedCaller,
        EntraAuthenticationError.ProviderUnavailable => SiloConnectionAuthenticationFailure.ProviderUnavailable,
        _ => SiloConnectionAuthenticationFailure.InvalidToken,
    };
}
