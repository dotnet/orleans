using System.Reflection;
using BenchmarkDotNet.Attributes;
using Benchmarks.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;

namespace Benchmarks.Serialization;

/// <summary>
/// Measures generated grain reference proxy construction, which runs on every GetGrain call and every grain reference deserialization.
/// </summary>
/// <remarks>
/// Each proxy type is constructed from the shared instance which its first construction produced, as a grain reference
/// activator does for every later reference.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
public class ProxyConstructionBenchmark
{
    private static readonly PropertyInfo SharedProperty = typeof(GrainReference).GetProperty("Shared", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly IdSpan _key = IdSpan.Create("key");
    private readonly GrainReferenceShared _noCopiers;
    private readonly GrainReferenceShared _concrete;
    private readonly GrainReferenceShared _interface;
    private readonly GrainReferenceShared _fourInterfaces;

    public ProxyConstructionBenchmark()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(ProxyConstructionBenchmark).Assembly))
            .BuildServiceProvider();
        var shared = new GrainReferenceShared(
            GrainType.Create("proxy-benchmark"),
            GrainInterfaceType.Create("proxy-benchmark"),
            interfaceVersion: 0,
            runtime: null!,
            InvokeMethodOptions.None,
            services.GetRequiredService<CodecProvider>(),
            services.GetRequiredService<CopyContextPool>(),
            services);
        _noCopiers = GetShared(new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyNoCopierGrain(shared, _key));
        _concrete = GetShared(new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyConcreteCopierGrain(shared, _key));
        _interface = GetShared(new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyInterfaceCopierGrain(shared, _key));
        _fourInterfaces = GetShared(new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyFourInterfaceCopiersGrain(shared, _key));
    }

    private static GrainReferenceShared GetShared(GrainReference reference) => (GrainReferenceShared)SharedProperty.GetValue(reference)!;

    [Benchmark(Baseline = true)]
    public object NoCopiers() => new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyNoCopierGrain(_noCopiers, _key);

    [Benchmark]
    public object OneConcreteCopier() => new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyConcreteCopierGrain(_concrete, _key);

    [Benchmark]
    public object OneInterfaceCopier() => new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyInterfaceCopierGrain(_interface, _key);

    [Benchmark]
    public object FourInterfaceCopiers() => new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyFourInterfaceCopiersGrain(_fourInterfaces, _key);
}

[GenerateSerializer]
public sealed class ProxyBenchmarkPayload
{
    [Id(0)] public int Value { get; set; }
}

public interface IProxyBenchmarkPayloadA;
public interface IProxyBenchmarkPayloadB;
public interface IProxyBenchmarkPayloadC;
public interface IProxyBenchmarkPayloadD;

public interface IProxyNoCopierGrain : IGrainWithStringKey
{
    Task Send(int value);
}

public interface IProxyConcreteCopierGrain : IGrainWithStringKey
{
    Task Send(ProxyBenchmarkPayload value);
}

public interface IProxyInterfaceCopierGrain : IGrainWithStringKey
{
    Task Send(IProxyBenchmarkPayloadA value);
}

public interface IProxyFourInterfaceCopiersGrain : IGrainWithStringKey
{
    Task Send(IProxyBenchmarkPayloadA a, IProxyBenchmarkPayloadB b, IProxyBenchmarkPayloadC c, IProxyBenchmarkPayloadD d);
}
