using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.TestKit;
using Orleans.Serialization.WireProtocol;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class SerializationTestKitContractTests(ITestOutputHelper output)
{
    [Fact]
    public void SerializationTester_NullOutput_ThrowsWithExactParamNameWithoutCreatingServices()
    {
        var probe = new ConstructionProbe();
        SerializationTesterHarness.Probe = probe;

        var exception = Assert.Throws<ArgumentNullException>(() => new SerializationTesterHarness(null!));

        Assert.Equal("output", exception.ParamName);
        Assert.Equal(0, probe.ServiceProviderFactoryCount);
    }

    [Fact]
    public void SerializationTester_NullFixture_ThrowsWithExactParamNameWithoutCreatingServices()
    {
        var probe = new ConstructionProbe();
        SerializationTesterHarness.Probe = probe;

        var exception = Assert.Throws<ArgumentNullException>(() => new SerializationTesterHarness(output, null!));

        Assert.Equal("fixture", exception.ParamName);
        Assert.Equal(0, probe.ServiceProviderFactoryCount);
    }

    [Fact]
    public void FieldCodecTester_FirstConstructor_NullOutput_ThrowsWithExactParamNameWithoutInvokingDependencies()
    {
        var probe = new ConstructionProbe();
        FieldCodecTesterHarness<object>.Probe = probe;

        var exception = Assert.Throws<ArgumentNullException>(() => new FieldCodecTesterHarness<object>(null!));

        Assert.Equal("output", exception.ParamName);
        AssertNoFieldCodecDependenciesWereInvoked(probe);
    }

    [Fact]
    public void FieldCodecTester_FixtureConstructor_NullOutput_ThrowsWithExactParamNameWithoutInvokingDependencies()
    {
        var probe = new ConstructionProbe();
        FieldCodecTesterHarness<object>.Probe = probe;
        using var fixture = new SerializationTesterFixture();

        var exception = Assert.Throws<ArgumentNullException>(() => new FieldCodecTesterHarness<object>(null!, fixture));

        Assert.Equal("output", exception.ParamName);
        AssertNoFieldCodecDependenciesWereInvoked(probe);
    }

    [Fact]
    public void FieldCodecTester_FixtureConstructor_NullFixture_ThrowsWithExactParamNameWithoutInvokingDependencies()
    {
        var probe = new ConstructionProbe();
        FieldCodecTesterHarness<object>.Probe = probe;

        var exception = Assert.Throws<ArgumentNullException>(() => new FieldCodecTesterHarness<object>(output, null!));

        Assert.Equal("fixture", exception.ParamName);
        AssertNoFieldCodecDependenciesWereInvoked(probe);
    }

    [Fact]
    public void CopierTester_FirstConstructor_NullOutput_ThrowsWithExactParamNameWithoutInvokingDependencies()
    {
        var probe = new ConstructionProbe();
        CopierTesterHarness<object>.Probe = probe;

        var exception = Assert.Throws<ArgumentNullException>(() => new CopierTesterHarness<object>(null!));

        Assert.Equal("output", exception.ParamName);
        AssertNoCopierDependenciesWereInvoked(probe);
    }

    [Fact]
    public void CopierTester_FixtureConstructor_NullOutput_ThrowsWithExactParamNameWithoutInvokingDependencies()
    {
        var probe = new ConstructionProbe();
        CopierTesterHarness<object>.Probe = probe;
        using var fixture = new SerializationTesterFixture();

        var exception = Assert.Throws<ArgumentNullException>(() => new CopierTesterHarness<object>(null!, fixture));

        Assert.Equal("output", exception.ParamName);
        AssertNoCopierDependenciesWereInvoked(probe);
    }

    [Fact]
    public void CopierTester_FixtureConstructor_NullFixture_ThrowsWithExactParamNameWithoutInvokingDependencies()
    {
        var probe = new ConstructionProbe();
        CopierTesterHarness<object>.Probe = probe;

        var exception = Assert.Throws<ArgumentNullException>(() => new CopierTesterHarness<object>(output, null!));

        Assert.Equal("fixture", exception.ParamName);
        AssertNoCopierDependenciesWereInvoked(probe);
    }

    [Fact]
    public void Batch_NullSequence_ThrowsWithExactParamNameAtCall()
    {
        IEnumerable<byte> sequence = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => sequence.Batch(batchSize: 2));

        Assert.Equal("sequence", exception.ParamName);
    }

    [Fact]
    public void ToReadOnlySequence_NullByteBuffers_ThrowsWithExactParamName()
    {
        IEnumerable<byte[]> buffers = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => buffers.ToReadOnlySequence());

        Assert.Equal("buffers", exception.ParamName);
    }

    [Fact]
    public void ToReadOnlySequence_NullMemoryBuffers_ThrowsWithExactParamName()
    {
        IEnumerable<Memory<byte>> buffers = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => buffers.ToReadOnlySequence());

        Assert.Equal("buffers", exception.ParamName);
    }

    [Fact]
    public void CreateReadOnlySequence_NullBuffers_ThrowsWithExactParamName()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => ReadOnlySequenceHelper.CreateReadOnlySequence((byte[][])null!));

        Assert.Equal("buffers", exception.ParamName);
    }

    private static void AssertNoFieldCodecDependenciesWereInvoked(ConstructionProbe probe)
    {
        Assert.Equal(0, probe.ServiceProviderFactoryCount);
        Assert.Equal(0, probe.ConfigureCount);
        Assert.Equal(0, probe.CodecCreationCount);
        Assert.Equal(0, probe.CodecWriteCount);
        Assert.Equal(0, probe.CodecReadCount);
    }

    private static void AssertNoCopierDependenciesWereInvoked(ConstructionProbe probe)
    {
        Assert.Equal(0, probe.ServiceProviderFactoryCount);
        Assert.Equal(0, probe.ConfigureCount);
        Assert.Equal(0, probe.CopierCreationCount);
        Assert.Equal(0, probe.CopyCount);
    }

    private sealed class ConstructionProbe
    {
        public int ServiceProviderFactoryCount { get; set; }

        public int ConfigureCount { get; set; }

        public int CodecCreationCount { get; set; }

        public int CodecWriteCount { get; set; }

        public int CodecReadCount { get; set; }

        public int CopierCreationCount { get; set; }

        public int CopyCount { get; set; }
    }

    private sealed class SerializationTesterHarness : SerializationTester
    {
        public SerializationTesterHarness(ITestOutputHelper output)
            : base(output)
        {
        }

        public SerializationTesterHarness(ITestOutputHelper output, SerializationTesterFixture fixture)
            : base(output, fixture)
        {
        }

        public static ConstructionProbe Probe { get; set; } = null!;

        protected override IServiceProvider CreateServiceProvider()
        {
            Probe.ServiceProviderFactoryCount++;
            return new EmptyServiceProvider();
        }
    }

    private sealed class FieldCodecTesterHarness<TMarker> : FieldCodecTester<int, CountingInt32Codec>
    {
        public FieldCodecTesterHarness(ITestOutputHelper output)
            : base(output)
        {
        }

        public FieldCodecTesterHarness(ITestOutputHelper output, SerializationTesterFixture fixture)
            : base(output, fixture)
        {
        }

        public static ConstructionProbe Probe { get; set; } = null!;

        protected override IServiceProvider CreateServiceProvider()
        {
            Probe.ServiceProviderFactoryCount++;
            return base.CreateServiceProvider();
        }

        protected override void Configure(ISerializerBuilder builder) => Probe.ConfigureCount++;

        protected override CountingInt32Codec CreateCodec()
        {
            Probe.CodecCreationCount++;
            return new CountingInt32Codec();
        }

        protected override int CreateValue() => 42;

        protected override int[] TestValues => [42];
    }

    private sealed class CopierTesterHarness<TMarker> : CopierTester<int, CountingInt32Copier>
    {
        public CopierTesterHarness(ITestOutputHelper output)
            : base(output)
        {
        }

        public CopierTesterHarness(ITestOutputHelper output, SerializationTesterFixture fixture)
            : base(output, fixture)
        {
        }

        public static ConstructionProbe Probe { get; set; } = null!;

        protected override IServiceProvider CreateServiceProvider()
        {
            Probe.ServiceProviderFactoryCount++;
            return base.CreateServiceProvider();
        }

        protected override void Configure(ISerializerBuilder builder) => Probe.ConfigureCount++;

        protected override CountingInt32Copier CreateCopier()
        {
            Probe.CopierCreationCount++;
            return new CountingInt32Copier();
        }

        protected override int CreateValue() => 42;

        protected override int[] TestValues => [42];
    }

    private sealed class CountingInt32Codec : IFieldCodec<int>
    {
        public void WriteField<TBufferWriter>(
            ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            [AllowNull] Type expectedType,
            int value)
            where TBufferWriter : IBufferWriter<byte>
        {
            FieldCodecTesterHarness<object>.Probe.CodecWriteCount++;
            throw new InvalidOperationException("The counting codec must not be invoked during constructor validation.");
        }

        public int ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            FieldCodecTesterHarness<object>.Probe.CodecReadCount++;
            throw new InvalidOperationException("The counting codec must not be invoked during constructor validation.");
        }
    }

    private sealed class CountingInt32Copier : IDeepCopier<int>
    {
        public int DeepCopy(int input, CopyContext context)
        {
            CopierTesterHarness<object>.Probe.CopyCount++;
            throw new InvalidOperationException("The counting copier must not be invoked during constructor validation.");
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
