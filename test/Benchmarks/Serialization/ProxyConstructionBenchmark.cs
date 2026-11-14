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
[Config(typeof(BenchmarkConfig))]
public class ProxyConstructionBenchmark
{
    private readonly GrainReferenceShared _shared;
    private readonly IdSpan _key = IdSpan.Create("key");

    public ProxyConstructionBenchmark()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(ProxyConstructionBenchmark).Assembly))
            .BuildServiceProvider();
        _shared = new GrainReferenceShared(
            GrainType.Create("proxy-benchmark"),
            GrainInterfaceType.Create("proxy-benchmark"),
            interfaceVersion: 0,
            runtime: null!,
            InvokeMethodOptions.None,
            services.GetRequiredService<CodecProvider>(),
            services.GetRequiredService<CopyContextPool>(),
            services);
    }

    [Benchmark(Baseline = true)]
    public object NoCopiers() => new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyNoCopierGrain(_shared, _key);

    [Benchmark]
    public object OneConcreteCopier() => new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyConcreteCopierGrain(_shared, _key);

    [Benchmark]
    public object OneInterfaceCopier() => new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyInterfaceCopierGrain(_shared, _key);

    [Benchmark]
    public object FourInterfaceCopiers() => new OrleansCodeGen.Benchmarks.Serialization.Proxy_IProxyFourInterfaceCopiersGrain(_shared, _key);
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
