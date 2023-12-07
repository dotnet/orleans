using System;
using System.Diagnostics.CodeAnalysis;

#nullable enable
namespace Orleans.Runtime;

/// <summary>
/// Represents a directive to update an invalid, cached <see cref="GrainAddress"/> to a valid <see cref="GrainAddress"/>.
/// </summary>
[GenerateSerializer, Immutable]
public sealed class GrainAddressCacheUpdate : ISpanFormattable
{
    [Id(0)]
    private readonly GrainId _grainId;

    [Id(1)]
    private readonly ActivationId _invalidActivationId;

    [Id(2)]
    private readonly SiloAddress? _invalidSiloAddress;

    [Id(3)]
    private readonly MembershipVersion _invalidMembershipVersion = MembershipVersion.MinValue;

    [Id(4)]
    private readonly ActivationId _validActivationId;

    [Id(5)]
    private readonly SiloAddress? _validSiloAddress;

    [Id(6)]
    private readonly MembershipVersion _validMembershipVersion = MembershipVersion.MinValue;

    public GrainAddressCacheUpdate(GrainAddress invalidAddress, GrainAddress? validAddress)
    {
        ArgumentNullException.ThrowIfNull(invalidAddress);

        _grainId = invalidAddress.GrainId;
        _invalidActivationId = invalidAddress.ActivationId;
        _invalidSiloAddress = invalidAddress.SiloAddress;
        _invalidMembershipVersion = invalidAddress.MembershipVersion;

        if (validAddress is not null)
        {
            if (invalidAddress.GrainId != validAddress.GrainId)
            {
                ThrowGrainIdDoesNotMatch(invalidAddress, validAddress);
                return;
            }

            _validActivationId = validAddress.ActivationId;
            _validSiloAddress = validAddress.SiloAddress;
            _validMembershipVersion = validAddress.MembershipVersion;
        }
    }

    /// <summary>
    /// Identifier of the Grain.
    /// </summary>
    public GrainId GrainId => _grainId;

    /// <summary>
    /// Identifier of the invalid grain activation.
    /// </summary>
    public ActivationId InvalidActivationId => _invalidActivationId;

    /// <summary>
    /// Address of the silo indicated by the invalid grain activation cache entry.
    /// </summary>
    public SiloAddress? InvalidSiloAddress => _invalidSiloAddress;

    /// <summary>
    /// Gets the valid grain activation address.
    /// </summary>
    public GrainAddress? ValidGrainAddress => _validSiloAddress switch
    {
        null => null,
        _ => new()
        {
            GrainId = _grainId,
            ActivationId = _validActivationId,
            SiloAddress = _validSiloAddress,
            MembershipVersion = _validMembershipVersion,
        }
    };

    /// <summary>
    /// Gets the invalid grain activation address.
    /// </summary>
    public GrainAddress InvalidGrainAddress => new()
    {
        GrainId = _grainId,
        ActivationId = _invalidActivationId,
        SiloAddress = _invalidSiloAddress,
        MembershipVersion = _invalidMembershipVersion,
    };

    public override string ToString() => $"[{nameof(GrainAddressCacheUpdate)} GrainId {_grainId}, InvalidActivationId: {_invalidActivationId}, InvalidSiloAddress: {_invalidSiloAddress}, ValidGrainAddress: {ValidGrainAddress}]";

    string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

    bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => destination.TryWrite($"[{nameof(GrainAddressCacheUpdate)} GrainId {_grainId}, InvalidActivationId: {_invalidActivationId}, InvalidSiloAddress: {_invalidSiloAddress}, ValidGrainAddress: {ValidGrainAddress}]", out charsWritten);

    public string ToFullString() => $"[{nameof(GrainAddressCacheUpdate)} GrainId {_grainId}, InvalidActivationId: {_invalidActivationId}, InvalidSiloAddress: {_invalidSiloAddress}, ValidGrainAddress: {ValidGrainAddress}, MembershipVersion: {_invalidMembershipVersion}]";

    [DoesNotReturn]
    private static void ThrowGrainIdDoesNotMatch(GrainAddress invalidAddress, GrainAddress validAddress) => throw new ArgumentException($"Invalid grain address grain id {invalidAddress.GrainId} does not match valid grain address grain id {validAddress.GrainId}.", nameof(validAddress));
}
