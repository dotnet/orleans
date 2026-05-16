namespace Orleans.Journaling.Tests;

/// <summary>
/// Compound test type used by the codec snapshot tests to exercise Orleans field-codec
/// behaviour for a multi-field record (string + int + nested struct). Stable
/// <see cref="GenerateSerializerAttribute"/> + <see cref="IdAttribute"/> ids pin the on-wire
/// shape so the snapshot files in <c>snapshots/</c> are reproducible.
/// </summary>
[GenerateSerializer]
public sealed record class JournalingSnapshotRecord
{
    [Id(0)]
    public string Name { get; init; } = string.Empty;

    [Id(1)]
    public int Count { get; init; }

    [Id(2)]
    public JournalingSnapshotRecordTag Tag { get; init; }
}

/// <summary>
/// Nested struct member of <see cref="JournalingSnapshotRecord"/>. Exists so the binary
/// snapshots exercise nested-field-codec wire layout, not just a flat scalar payload.
/// </summary>
[GenerateSerializer]
public readonly record struct JournalingSnapshotRecordTag
{
    [Id(0)]
    public string Label { get; init; }

    [Id(1)]
    public int Code { get; init; }
}
