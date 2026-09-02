using System.Security.Cryptography;
using System.Text;

namespace Orleans.Runtime.ClusterServices;

internal sealed class ClusterServiceConfiguration
{
    public ClusterServiceConfiguration(
        string serviceId,
        int protocolVersion,
        int partitionsPerSilo,
        string assignmentStrategy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(protocolVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionsPerSilo, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignmentStrategy);

        ServiceId = serviceId;
        ProtocolVersion = protocolVersion;
        PartitionsPerSilo = partitionsPerSilo;
        AssignmentStrategy = assignmentStrategy;
        Fingerprint = ComputeFingerprint(serviceId, protocolVersion, partitionsPerSilo, assignmentStrategy);
    }

    public string ServiceId { get; }

    public int ProtocolVersion { get; }

    public int PartitionsPerSilo { get; }

    public string AssignmentStrategy { get; }

    public string Fingerprint { get; }

    private static string ComputeFingerprint(
        string serviceId,
        int protocolVersion,
        int partitionsPerSilo,
        string assignmentStrategy)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(serviceId);
            writer.Write(protocolVersion);
            writer.Write(partitionsPerSilo);
            writer.Write(assignmentStrategy);
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

[GenerateSerializer, Immutable, Alias(nameof(ClusterServiceViewId))]
internal readonly record struct ClusterServiceViewId(
    [property: Id(0)] MembershipVersion MembershipVersion,
    [property: Id(1)] int ProtocolVersion,
    [property: Id(2)] string ConfigurationFingerprint)
{
    public bool IsDirectSuccessorOf(ClusterServiceViewId previous) =>
        ProtocolVersion == previous.ProtocolVersion
        && StringComparer.Ordinal.Equals(ConfigurationFingerprint, previous.ConfigurationFingerprint)
        && MembershipVersion.Value == previous.MembershipVersion.Value + 1;
}
