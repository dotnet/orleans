using System;
using System.Text.Json.Serialization;

namespace Orleans.Runtime;

/// <summary>
/// Identifies an Orleans addressable object within a service.
/// </summary>
[Serializable, GenerateSerializer, Immutable]
[Alias("universal-ref")]
public readonly struct UniversalReference : IEquatable<UniversalReference>, ISpanFormattable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UniversalReference"/> struct.
    /// </summary>
    /// <param name="grainId">The grain identity.</param>
    /// <param name="interfaceType">The grain interface represented by this reference.</param>
    /// <param name="serviceId">The Orleans service identity.</param>
    /// <param name="binding">The reference binding.</param>
    /// <param name="clusterId">The bound cluster identity, if applicable.</param>
    [JsonConstructor]
    public UniversalReference(
        GrainId grainId,
        GrainInterfaceType interfaceType,
        string serviceId,
        UniversalReferenceBinding binding,
        string? clusterId)
    {
        if (grainId.IsDefault)
        {
            throw new ArgumentException("The grain identity must be initialized.", nameof(grainId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        if (binding is not (UniversalReferenceBinding.Virtual or UniversalReferenceBinding.Cluster))
        {
            throw new ArgumentOutOfRangeException(nameof(binding));
        }

        if (binding == UniversalReferenceBinding.Cluster)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        }
        else if (clusterId is not null)
        {
            throw new ArgumentException("A virtual reference cannot specify a cluster identity.", nameof(clusterId));
        }

        GrainId = grainId;
        InterfaceType = interfaceType;
        ServiceId = serviceId;
        Binding = binding;
        ClusterId = clusterId;
    }

    /// <summary>
    /// Gets the grain identity.
    /// </summary>
    [Id(0)]
    public GrainId GrainId { get; }

    /// <summary>
    /// Gets the grain interface represented by this reference.
    /// </summary>
    [Id(1)]
    public GrainInterfaceType InterfaceType { get; }

    /// <summary>
    /// Gets the Orleans service identity.
    /// </summary>
    [Id(2)]
    public string ServiceId { get; }

    /// <summary>
    /// Gets the reference binding.
    /// </summary>
    [Id(3)]
    public UniversalReferenceBinding Binding { get; }

    /// <summary>
    /// Gets the bound cluster identity, if applicable.
    /// </summary>
    [Id(4)]
    public string? ClusterId { get; }

    /// <summary>
    /// Gets a value indicating whether this value is uninitialized.
    /// </summary>
    public bool IsDefault => GrainId.IsDefault;

    /// <summary>
    /// Creates a virtual reference.
    /// </summary>
    public static UniversalReference CreateVirtual(GrainId grainId, GrainInterfaceType interfaceType, string serviceId)
        => new(grainId, interfaceType, serviceId, UniversalReferenceBinding.Virtual, clusterId: null);

    /// <summary>
    /// Creates a cluster-bound reference.
    /// </summary>
    public static UniversalReference CreateCluster(
        GrainId grainId,
        GrainInterfaceType interfaceType,
        string serviceId,
        string clusterId)
        => new(grainId, interfaceType, serviceId, UniversalReferenceBinding.Cluster, clusterId);

    /// <summary>
    /// Returns a copy of this value representing another grain interface.
    /// </summary>
    public UniversalReference WithInterfaceType(GrainInterfaceType interfaceType)
        => new(GrainId, interfaceType, ServiceId, Binding, ClusterId);

    /// <summary>
    /// Validates this value.
    /// </summary>
    internal void Validate()
    {
        if (GrainId.IsDefault)
        {
            throw new ArgumentException("The grain identity must be initialized.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceId);

        switch (Binding)
        {
            case UniversalReferenceBinding.Virtual when ClusterId is null:
                return;
            case UniversalReferenceBinding.Cluster when !string.IsNullOrWhiteSpace(ClusterId):
                return;
            default:
                throw new ArgumentException("The universal reference binding is invalid.");
        }
    }

    /// <inheritdoc/>
    public bool Equals(UniversalReference other)
        => GrainId.Equals(other.GrainId)
            && Binding == other.Binding
            && string.Equals(ServiceId, other.ServiceId, StringComparison.Ordinal)
            && string.Equals(ClusterId, other.ClusterId, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is UniversalReference other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Binding == UniversalReferenceBinding.Virtual
        ? GrainId.GetHashCode()
        : HashCode.Combine(
            GrainId,
            Binding,
            StringComparer.Ordinal.GetHashCode(ServiceId ?? string.Empty),
            ClusterId is null ? 0 : StringComparer.Ordinal.GetHashCode(ClusterId));

    /// <summary>
    /// Returns a stable, uniformly distributed hash code.
    /// </summary>
    public uint GetUniformHashCode()
    {
        if (Binding == UniversalReferenceBinding.Virtual)
        {
            return GrainId.GetUniformHashCode();
        }

        var result = GrainId.GetUniformHashCode();
        result = (result * 31) + StableHash.ComputeHash(ServiceId ?? string.Empty);
        result = (result * 31) + (uint)Binding;
        if (ClusterId is not null)
        {
            result = (result * 31) + StableHash.ComputeHash(ClusterId);
        }

        return result;
    }

    /// <inheritdoc/>
    public override string ToString() => Binding switch
    {
        UniversalReferenceBinding.Virtual => $"{ServiceId}/virtual/{GrainId}:{InterfaceType}",
        UniversalReferenceBinding.Cluster => $"{ServiceId}/{ClusterId}/{GrainId}:{InterfaceType}",
        _ => $"{ServiceId}/unknown/{GrainId}:{InterfaceType}"
    };

    /// <inheritdoc/>
    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc/>
    bool ISpanFormattable.TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider)
    {
        var value = ToString();
        if (value.AsSpan().TryCopyTo(destination))
        {
            charsWritten = value.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    /// <summary>
    /// Compares two values for equality.
    /// </summary>
    public static bool operator ==(UniversalReference left, UniversalReference right) => left.Equals(right);

    /// <summary>
    /// Compares two values for inequality.
    /// </summary>
    public static bool operator !=(UniversalReference left, UniversalReference right) => !left.Equals(right);
}

/// <summary>
/// Describes how an Orleans reference is bound to a cluster.
/// </summary>
[GenerateSerializer]
public enum UniversalReferenceBinding : byte
{
    /// <summary>
    /// The target cluster is resolved using the configured <see cref="IClusterLocator"/>.
    /// </summary>
    Virtual,

    /// <summary>
    /// The target belongs to the cluster identified by <see cref="UniversalReference.ClusterId"/>.
    /// </summary>
    Cluster
}
