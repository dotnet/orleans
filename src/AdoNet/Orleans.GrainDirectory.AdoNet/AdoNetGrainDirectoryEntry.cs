using System;
using Orleans.Runtime;

namespace Orleans.GrainDirectory.AdoNet;

/// <summary>
/// The model that represents a grain activation in an ADONET grain directory.
/// </summary>
internal sealed record AdoNetGrainDirectoryEntry(
    string ClusterId,
    string GrainId,
    string SiloAddress,
    string ActivationId)
{
    public AdoNetGrainDirectoryEntry() : this("", "", "", "")
    {
    }

    public GrainAddress ToGrainAddress() => new()
    {
        GrainId = Runtime.GrainId.Parse(GrainId),
        SiloAddress = Runtime.SiloAddress.FromParsableString(SiloAddress),
        ActivationId = Runtime.ActivationId.FromParsableString(ActivationId)
    };

    public static AdoNetGrainDirectoryEntry FromGrainAddress(string clusterId, GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(clusterId);
        ArgumentNullException.ThrowIfNull(address);

        return new AdoNetGrainDirectoryEntry(
            clusterId,
            address.GrainId.ToString(),
            address.SiloAddress.ToParsableString(),
            address.ActivationId.ToParsableString());
    }
}
