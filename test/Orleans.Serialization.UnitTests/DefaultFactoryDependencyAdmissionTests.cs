using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class DefaultFactoryDependencyAdmissionTests
{
    [Theory]
    [InlineData("Instance", false)]
    [InlineData("Instance", true)]
    [InlineData("Factory", false)]
    [InlineData("Factory", true)]
    [InlineData("Type", false)]
    [InlineData("Type", true)]
    public void ExternalDependency_DeclinesDefaultGraphAndUsesBuiltProvider(string registration, bool mutateAfterBuild)
    {
        var original = new Dependency();
        var replacement = new Dependency();
        var state = new State();
        var collection = new ServiceCollection();
        if (registration == "Instance") collection.AddSingleton(original);
        else if (registration == "Factory") collection.AddSingleton(_ => original);
        else collection.AddSingleton<Dependency>();
        collection.AddSerializer(builder => builder.Configure(options => RegisterDefaults(options, state)));
        using var services = collection.BuildServiceProvider();
        if (mutateAfterBuild) collection.AddSingleton(replacement);
        var provider = services.GetRequiredService<CodecProvider>();

        var codec = Assert.IsType<DependentCodec>(provider.GetCodec<Target>());
        var actual = services.GetRequiredService<Dependency>();

        Assert.Equal(0, state.FactoryCalls);
        Assert.Same(actual, codec.Dependency);
        Assert.NotSame(replacement, codec.Dependency);
        Assert.False(codec.ConstructionWasPending);
        Assert.Same(actual, codec.Services.GetRequiredService<Dependency>());
        Assert.Same(codec, provider.GetCodec<Target>());
        Assert.False(provider.IsConstructionPending);
    }

    [Fact]
    public void ExplicitClosedDependency_PublishesCanonicalIdentity()
    {
        var dependency = new Dependency();
        var state = new State();
        using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
        {
            options.AddSerializerService<Dependency>(_ => dependency);
            RegisterDefaults(options, state);
        })).BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();

        var codec = Assert.IsType<DependentCodec>(provider.GetCodec<Target>());

        Assert.Equal(1, state.FactoryCalls);
        Assert.True(codec.ConstructionWasPending);
        Assert.Same(dependency, codec.Dependency);
        Assert.Same(dependency, OrleansGeneratedCodeHelper.GetService<Dependency>(null!, provider));
        Assert.Same(codec, OrleansGeneratedCodeHelper.GetService<DependentCodec>(null!, provider));
        Assert.Same(codec, provider.GetCodec<Target>());
        Assert.False(provider.IsConstructionPending);
    }

    [Fact]
    public void ExplicitClosedDependency_FailedGraphRollsBackBeforeCanonicalRetry()
    {
        var dependency = new Dependency();
        var dependencyCalls = 0;
        var state = new State();
        var failure = new InvalidOperationException("graph failed after constructing codec");
        using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
        {
            options.AddSerializerService<Dependency>(_ =>
            {
                dependencyCalls++;
                return dependency;
            });
            RegisterDefaults(options, state);
            options.AddSerializerService<FailingRoot>(provider =>
            {
                _ = provider.GetCodec<Target>();
                throw failure;
            });
        })).BuildServiceProvider();
        var provider = services.GetRequiredService<CodecProvider>();

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => OrleansGeneratedCodeHelper.GetService<FailingRoot>(null!, provider)));
        Assert.False(provider.IsConstructionPending);
        Assert.Equal(1, state.FactoryCalls);
        Assert.Equal(1, dependencyCalls);
        var rolledBack = state.LastCodec;

        var codec = Assert.IsType<DependentCodec>(provider.GetCodec<Target>());

        Assert.Equal(2, state.FactoryCalls);
        Assert.Equal(2, dependencyCalls);
        Assert.NotSame(rolledBack, codec);
        Assert.Same(dependency, codec.Dependency);
        Assert.Same(codec, OrleansGeneratedCodeHelper.GetService<DependentCodec>(null!, provider));
        Assert.False(provider.IsConstructionPending);
    }

    private static void RegisterDefaults(TypeManifestOptions options, State state)
    {
        options.AddSerializer(typeof(DependentCodec));
        options.AddDefaultSerializerService<DependentCodec, DependentCodec>(provider =>
        {
            state.FactoryCalls++;
            return state.LastCodec = new DependentCodec(
                OrleansGeneratedCodeHelper.GetService<Dependency>(null!, provider), ((CodecProvider)provider).Services);
        }, dependencies: [typeof(Dependency), typeof(IServiceProvider)]);
        options.AddDefaultSerializer<Target, DependentCodec, ShallowCopier<Target>>(
            provider => OrleansGeneratedCodeHelper.GetService<DependentCodec>(null!, provider),
            static _ => new ShallowCopier<Target>(), codecDependencies: [typeof(Dependency), typeof(IServiceProvider)]);
    }

    private sealed class State
    {
        public int FactoryCalls;
        public DependentCodec? LastCodec;
    }

    public sealed class Dependency
    {
    }

    public sealed class Target
    {
    }

    private sealed class FailingRoot
    {
    }

    public sealed class DependentCodec : IFieldCodec<Target>
    {
        public DependentCodec(Dependency dependency, IServiceProvider services)
        {
            Dependency = dependency;
            Services = services;
            ConstructionWasPending = services.GetRequiredService<CodecProvider>().IsConstructionPending;
        }

        public Dependency Dependency { get; }
        public IServiceProvider Services { get; }
        public bool ConstructionWasPending { get; }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, Target? value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> => throw new NotSupportedException();

        public Target ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException();
    }
}
