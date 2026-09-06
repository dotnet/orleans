using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.TestKit;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class FieldCodecTesterBorrowedOwnershipTests(ITestOutputHelper output)
{
    [Fact]
    public void DisposingFixtureBackedTester_DoesNotDisposeSharedSerializerServices()
    {
        const int original = 0x1234_5678;
        var fixture = new SerializationTesterFixture();
        FixtureBackedInt32Tester? firstTester = null;
        FixtureBackedInt32Tester? secondTester = null;

        try
        {
            firstTester = new FixtureBackedInt32Tester(output, fixture);

            try
            {
                secondTester = new FixtureBackedInt32Tester(output, fixture);
                Assert.Same(firstTester.SharedSessionPool, secondTester.SharedSessionPool);
            }
            finally
            {
                ((IDisposable)firstTester).Dispose();
                firstTester = null;
            }

            var result = secondTester.RoundTrip(original);

            Assert.Equal(original, result);
        }
        finally
        {
            ((IDisposable?)firstTester)?.Dispose();
            ((IDisposable?)secondTester)?.Dispose();
            ((IDisposable)fixture).Dispose();
        }
    }

    private sealed class FixtureBackedInt32Tester(
        ITestOutputHelper output,
        SerializationTesterFixture fixture)
        : FieldCodecTester<int, UnusedInt32Codec>(output, fixture)
    {
        public SerializerSessionPool SharedSessionPool => SessionPool;

        public int RoundTrip(int value)
        {
            var serializer = ServiceProvider.GetRequiredService<Serializer<int>>();
            return serializer.Deserialize(serializer.SerializeToArray(value));
        }

        protected override IServiceProvider CreateServiceProvider()
        {
            var services = new ServiceCollection();
            services.AddSerializer();
            return services.BuildServiceProvider();
        }

        protected override int CreateValue() => 1;

        protected override int[] TestValues => [1];
    }

    private abstract class UnusedInt32Codec : IFieldCodec<int>
    {
        public abstract void WriteField<TBufferWriter>(
            ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            [AllowNull] Type expectedType,
            int value)
            where TBufferWriter : IBufferWriter<byte>;

        public abstract int ReadValue<TInput>(ref Reader<TInput> reader, Field field);
    }
}
