using System.Net;
using Microsoft.Extensions.Configuration;
using Orleans.Connections.Security;

namespace AuthenticatedSiloConnections;

internal sealed class SampleOptions
{
    public const string SectionName = "OrleansSecurity";

    public string ServiceId { get; set; } = "authenticated-silo-sample";

    public string ClusterId { get; set; } = "";

    public int SiloPort { get; set; } = 11111;

    public int GatewayPort { get; set; } = 30000;

    public int PrimarySiloPort { get; set; } = 11111;

    public SiloConnectionAuthenticationMode AuthenticationMode { get; set; }
        = SiloConnectionAuthenticationMode.Audit;

    public CertificateOptions Certificate { get; set; } = new();

    public EntraOptions Entra { get; set; } = new();

    public IPEndPoint PrimarySiloEndpoint
        => new(IPAddress.Loopback, PrimarySiloPort);

    public static SampleOptions Load(IConfiguration configuration)
    {
        var result = configuration
            .GetRequiredSection(SectionName)
            .Get<SampleOptions>()
            ?? throw new InvalidOperationException(
                $"Configuration section '{SectionName}' is required.");

        result.Validate();
        return result;
    }

    private void Validate()
    {
        RequireValue(ServiceId, nameof(ServiceId));
        RequireValue(ClusterId, nameof(ClusterId));
        ValidatePort(SiloPort, nameof(SiloPort));
        ValidatePort(GatewayPort, nameof(GatewayPort));
        ValidatePort(PrimarySiloPort, nameof(PrimarySiloPort));
        Certificate.Validate();
        Entra.Validate(ClusterId);
    }

    private static void ValidatePort(int value, string name)
    {
        if (value is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new InvalidOperationException($"{name} is outside the valid port range.");
        }
    }

    internal static void RequireValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('<')
            || value.Contains('>'))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be explicitly configured.");
        }
    }
}

internal sealed class CertificateOptions
{
    public string Path { get; set; } = "";

    public string? Password { get; set; }

    public string TargetHost { get; set; } = "";

    public string[] TrustedRootSha256Fingerprints { get; set; } = [];

    public void Validate()
    {
        SampleOptions.RequireValue(Path, "Certificate:Path");
        SampleOptions.RequireValue(TargetHost, "Certificate:TargetHost");

        if (!File.Exists(Path))
        {
            throw new InvalidOperationException(
                "The configured silo certificate file does not exist.");
        }

        if (TrustedRootSha256Fingerprints.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one trusted root SHA-256 fingerprint is required.");
        }

        _ = CertificatePolicy.ParseSha256Fingerprints(
            TrustedRootSha256Fingerprints);
    }
}

internal sealed class EntraOptions
{
    public string TenantId { get; set; } = "";

    public string ResourceApplicationId { get; set; } = "";

    public string WorkloadClientId { get; set; } = "";

    public string FederatedTokenFile { get; set; } = "";

    public string[] AllowedCallerClientIds { get; set; } = [];

    public Uri Authority
        => new($"https://login.microsoftonline.com/{TenantId}/v2.0");

    public string Audience
        => $"api://{ResourceApplicationId}/{_clusterId}";

    private string _clusterId = "";

    public void Validate(string clusterId)
    {
        _clusterId = clusterId;
        RequireGuid(TenantId, nameof(TenantId));
        RequireGuid(ResourceApplicationId, nameof(ResourceApplicationId));
        RequireGuid(WorkloadClientId, nameof(WorkloadClientId));
        SampleOptions.RequireValue(FederatedTokenFile, nameof(FederatedTokenFile));

        if (!File.Exists(FederatedTokenFile))
        {
            throw new InvalidOperationException(
                "The configured workload identity token file does not exist.");
        }

        if (AllowedCallerClientIds.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one allowed caller application ID is required.");
        }

        foreach (var clientId in AllowedCallerClientIds)
        {
            RequireGuid(clientId, nameof(AllowedCallerClientIds));
        }

        if (!AllowedCallerClientIds.Contains(
            WorkloadClientId,
            StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This silo's workload client ID must be in the allowed caller list.");
        }
    }

    private static void RequireGuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out _))
        {
            throw new InvalidOperationException(
                $"{SampleOptions.SectionName}:Entra:{name} must be a GUID.");
        }
    }
}
