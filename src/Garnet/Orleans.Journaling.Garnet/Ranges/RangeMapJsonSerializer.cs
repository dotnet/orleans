using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace Orleans.Journaling.Ranges;

/// <summary>
/// Serializes range map snapshots as JSON documents.
/// </summary>
/// <remarks>
/// The document records its format version, the map version, and the map. Partitions are written in id order, so
/// equal snapshots serialize to equal bytes.
/// </remarks>
internal static class RangeMapJsonSerializer
{
    private const int FormatVersion = 1;

    public static byte[] Serialize(RangeMapSnapshot snapshot)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            var map = snapshot.Map;
            writer.WriteStartObject();
            writer.WriteNumber("format", FormatVersion);
            writer.WriteNumber("version", snapshot.Version);
            writer.WriteNumber("bucketCount", map.BucketCount);
            writer.WriteStartArray("ranges");
            foreach (var range in map.Ranges)
            {
                writer.WriteStartObject();
                writer.WriteNumber("first", range.FirstBucket);
                writer.WriteNumber("last", range.LastBucket);
                writer.WriteString("owner", range.Owner.Value);
                writer.WriteNumber("assignedAt", range.AssignedAtVersion);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("migrations");
            foreach (var migration in map.Migrations)
            {
                writer.WriteStartObject();
                writer.WriteNumber("first", migration.FirstBucket);
                writer.WriteNumber("last", migration.LastBucket);
                writer.WriteString("source", migration.Source.Value);
                writer.WriteString("destination", migration.Destination.Value);
                writer.WriteNumber("startedAt", migration.StartedAtVersion);
                writer.WriteString("phase", FormatPhase(migration.Phase));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("partitions");
            foreach (var (id, hosting) in map.Partitions)
            {
                writer.WriteStartObject();
                writer.WriteString("id", id.Value);
                writer.WriteString("host", hosting.Host);
                writer.WriteNumber("epoch", hosting.Epoch);
                writer.WriteString("storage", hosting.StorageLocation);
                if (hosting.Endpoint is { } endpoint)
                {
                    writer.WriteString("endpoint", endpoint);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Deserializes and validates a snapshot.
    /// </summary>
    /// <exception cref="InvalidDataException">The document is malformed or describes an invalid map.</exception>
    public static RangeMapSnapshot Deserialize(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != FormatVersion)
            {
                throw new InvalidDataException($"The range map document has unsupported format {root.GetProperty("format")}.");
            }

            var version = root.GetProperty("version").GetInt64();
            if (version < 1)
            {
                throw new InvalidDataException($"The range map document has invalid version {version}.");
            }

            var ranges = ImmutableArray.CreateBuilder<RangeAssignment>();
            foreach (var element in root.GetProperty("ranges").EnumerateArray())
            {
                ranges.Add(new RangeAssignment(
                    element.GetProperty("first").GetInt32(),
                    element.GetProperty("last").GetInt32(),
                    new PartitionId(element.GetProperty("owner").GetString()!),
                    element.GetProperty("assignedAt").GetInt64()));
            }

            var migrations = ImmutableArray.CreateBuilder<Migration>();
            foreach (var element in root.GetProperty("migrations").EnumerateArray())
            {
                migrations.Add(new Migration(
                    element.GetProperty("first").GetInt32(),
                    element.GetProperty("last").GetInt32(),
                    new PartitionId(element.GetProperty("source").GetString()!),
                    new PartitionId(element.GetProperty("destination").GetString()!),
                    element.GetProperty("startedAt").GetInt64(),
                    ParsePhase(element.GetProperty("phase").GetString())));
            }

            var partitions = ImmutableSortedDictionary.CreateBuilder<PartitionId, PartitionHosting>();
            foreach (var element in root.GetProperty("partitions").EnumerateArray())
            {
                var id = new PartitionId(element.GetProperty("id").GetString()!);
                var hosting = new PartitionHosting(
                    element.GetProperty("host").GetString()!,
                    element.GetProperty("epoch").GetInt64(),
                    element.GetProperty("storage").GetString()!,
                    element.TryGetProperty("endpoint", out var endpoint) ? endpoint.GetString() : null);
                if (!partitions.TryAdd(id, hosting))
                {
                    throw new InvalidDataException($"The range map document lists partition '{id}' more than once.");
                }
            }

            var map = new RangeMap(root.GetProperty("bucketCount").GetInt32(), ranges.ToImmutable(), migrations.ToImmutable(), partitions.ToImmutable());
            map.Validate();
            return new RangeMapSnapshot(version, map);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException($"The range map document is invalid: {exception.Message}", exception);
        }
    }

    private static string FormatPhase(MigrationPhase phase) => phase switch
    {
        MigrationPhase.Committed => "committed",
        MigrationPhase.Sealed => "sealed",
        MigrationPhase.Opened => "opened",
        MigrationPhase.Done => "done",
        MigrationPhase.Complete => "complete",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown migration phase."),
    };

    private static MigrationPhase ParsePhase(string? phase) => phase switch
    {
        "committed" => MigrationPhase.Committed,
        "sealed" => MigrationPhase.Sealed,
        "opened" => MigrationPhase.Opened,
        "done" => MigrationPhase.Done,
        "complete" => MigrationPhase.Complete,
        _ => throw new FormatException($"Unknown migration phase '{phase}'."),
    };
}
