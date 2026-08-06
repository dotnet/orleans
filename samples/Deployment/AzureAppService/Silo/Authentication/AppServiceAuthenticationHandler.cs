// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Orleans.ShoppingCart.Silo.Authentication;

internal sealed class AppServiceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string ClientPrincipalHeader = "X-MS-CLIENT-PRINCIPAL";
    private const string MicrosoftEntraIdentityProvider = "aad";
    private const int MaximumHeaderLength = 16 * 1024;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ClientPrincipalHeader, out var header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (header.Count != 1
            || string.IsNullOrWhiteSpace(header[0])
            || header[0]!.Length > MaximumHeaderLength)
        {
            return Task.FromResult(
                AuthenticateResult.Fail("The App Service principal header is invalid."));
        }

        byte[] principalJson;
        try
        {
            principalJson = Convert.FromBase64String(header[0]!);
        }
        catch (FormatException exception)
        {
            return Task.FromResult(
                AuthenticateResult.Fail(
                    new InvalidOperationException(
                        "The App Service principal header is not valid Base64.",
                        exception)));
        }

        ClientPrincipal? principal;
        try
        {
            principal = JsonSerializer.Deserialize<ClientPrincipal>(principalJson);
        }
        catch (JsonException exception)
        {
            return Task.FromResult(
                AuthenticateResult.Fail(
                    new InvalidOperationException(
                        "The App Service principal header is not valid JSON.",
                        exception)));
        }

        if (principal is null
            || !string.Equals(
                principal.IdentityProvider,
                MicrosoftEntraIdentityProvider,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(principal.NameClaimType)
            || string.IsNullOrWhiteSpace(principal.RoleClaimType)
            || principal.Claims is null)
        {
            return Task.FromResult(
                AuthenticateResult.Fail("The App Service principal is incomplete."));
        }

        var claims = principal.Claims
            .Where(claim => !string.IsNullOrWhiteSpace(claim.Type)
                && !string.IsNullOrWhiteSpace(claim.Value))
            .Select(claim => new Claim(claim.Type!, claim.Value!));
        var identity = new ClaimsIdentity(
            claims,
            Scheme.Name,
            principal.NameClaimType,
            principal.RoleClaimType);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var returnUrl = Uri.EscapeDataString(
            $"{Request.PathBase}{Request.Path}{Request.QueryString}");
        Response.Redirect(
            $"/.auth/login/aad?post_login_redirect_uri={returnUrl}");

        return Task.CompletedTask;
    }

    private sealed class ClientPrincipal
    {
        [JsonPropertyName("auth_typ")]
        public string? IdentityProvider { get; init; }

        [JsonPropertyName("name_typ")]
        public string? NameClaimType { get; init; }

        [JsonPropertyName("role_typ")]
        public string? RoleClaimType { get; init; }

        [JsonPropertyName("claims")]
        public IReadOnlyList<ClientPrincipalClaim>? Claims { get; init; }
    }

    private sealed class ClientPrincipalClaim
    {
        [JsonPropertyName("typ")]
        public string? Type { get; init; }

        [JsonPropertyName("val")]
        public string? Value { get; init; }
    }
}
