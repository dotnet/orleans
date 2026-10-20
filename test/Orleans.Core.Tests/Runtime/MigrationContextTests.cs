using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using TestExtensions;
using Xunit;

namespace Orleans.Core.Tests.Runtime;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public sealed class MigrationContextTests
{
    [Fact]
    public void SerializationTransfersBufferOwnershipAndPreservesTheWireFormat()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var sessions = services.GetRequiredService<SerializerSessionPool>();
        var payload = Enumerable.Repeat((byte)0x5A, 20_000).ToArray();
        using var context = new MigrationContext(sessions);
        Assert.True(context.TryAddValue("payload", payload));
        Assert.True(context.TryGetBytes("payload", out var serializedValue));
        var valueBytes = serializedValue.ToArray();
        var legacy = new LegacyMigrationContext
        {
            Indices = new(StringComparer.Ordinal) { ["payload"] = (0, valueBytes.Length) },
        };
        legacy.Buffer.Write(valueBytes);
        var legacySerializer = services.GetRequiredService<Serializer<LegacyMigrationContext>>();
        var modernSerializer = services.GetRequiredService<Serializer<MigrationContext>>();
        var legacyBytes = legacySerializer.SerializeToArray(legacy);
        var modernBytes = modernSerializer.SerializeToArray(context);
        Assert.Equal(legacyBytes, modernBytes);
        Assert.Equal(0, context.SerializedBuffer.Length);

        // Both versions read the same fields. The source context can be disposed after serialization without returning its sent buffer twice.
        context.Dispose();
        using var modernCopy = Assert.IsType<MigrationContext>(modernSerializer.Deserialize(legacyBytes));
        Assert.True(modernCopy.TryGetValue("payload", out byte[]? actual));
        Assert.Equal(payload, actual);
        var legacyCopy = Assert.IsType<LegacyMigrationContext>(legacySerializer.Deserialize(modernBytes));
        try
        {
            Assert.Equal((0, valueBytes.Length), legacyCopy.Indices["payload"]);
            Assert.Equal(valueBytes, legacyCopy.Buffer.ToArray());
        }
        finally
        {
            legacyCopy.Buffer.Dispose();
        }
    }

    [GenerateSerializer]
    public sealed class LegacyMigrationContext
    {
        [Id(0), Immutable]
        public Dictionary<string, (int Offset, int Length)> Indices { get; set; } = new();

        [Id(1), Immutable]
        public PooledBuffer Buffer;
    }
}
