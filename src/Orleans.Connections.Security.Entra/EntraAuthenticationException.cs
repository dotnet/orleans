using System;

namespace Orleans.Connections.Security.Entra;

internal enum EntraAuthenticationError
{
    InvalidToken,
    ExpiredToken,
    UnauthorizedCaller,
    ProviderUnavailable,
    TokenAcquisitionFailed,
}

internal sealed class EntraAuthenticationException : Exception
{
    public EntraAuthenticationException(EntraAuthenticationError error)
        : base(GetMessage(error))
    {
        Error = error;
    }

    public EntraAuthenticationError Error { get; }

    private static string GetMessage(EntraAuthenticationError error) => error switch
    {
        EntraAuthenticationError.InvalidToken => "The Entra token is invalid.",
        EntraAuthenticationError.ExpiredToken => "The Entra token lifetime is invalid.",
        EntraAuthenticationError.UnauthorizedCaller => "The Entra caller is not authorized.",
        EntraAuthenticationError.ProviderUnavailable => "Entra metadata is unavailable.",
        EntraAuthenticationError.TokenAcquisitionFailed => "An Entra token could not be acquired.",
        _ => "Entra authentication failed.",
    };
}
