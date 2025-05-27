namespace Orleans.GrainDirectory.AdoNet;

/// <summary>
/// The model that represents a grain activation in an ADONET grain directory.
/// </summary>
internal sealed record AdoNetGrainDirectoryEntry(
    string ClusterId,
    string ProviderId,
    string GrainId,
    string SiloAddress,
    string ActivationId)
{
    public AdoNetGrainDirectoryEntry() : this("", "", "", "", "")
    {
    }

    public GrainAddress ToGrainAddress() => new()
    {
        GrainId = Runtime.GrainId.Parse(GrainId, CultureInfo.InvariantCulture),
        SiloAddress = Runtime.SiloAddress.FromParsableString(SiloAddress),
        ActivationId = Runtime.ActivationId.FromParsableString(ActivationId)
    };

    public static AdoNetGrainDirectoryEntry FromGrainAddress(string clusterId, string providerId, GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(clusterId);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(address.SiloAddress);

        return new AdoNetGrainDirectoryEntry(
            clusterId,
            providerId,
            address.GrainId.ToString(),
            address.SiloAddress.ToParsableString(),
            address.ActivationId.ToParsableString());
    }
}
