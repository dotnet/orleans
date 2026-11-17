using BenchmarkDotNet.Attributes;
using Benchmarks.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;

namespace Benchmarks.Serialization;

/// <summary>
/// Measures generated grain reference proxy construction, which runs on every GetGrain call and every grain reference deserialization.
/// </summary>
/// <remarks>
/// Proxies are created by their generated activators, as the runtime creates them.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
public class ProxyConstructionBenchmark
{
    private readonly GrainId _grainId = GrainId.Create("proxy-benchmark", "key");
    private readonly IGrainReferenceActivator _noCopiers;
    private readonly IGrainReferenceActivator _concrete;
    private readonly IGrainReferenceActivator _interface;
    private readonly IGrainReferenceActivator _fourInterfaces;

    public ProxyConstructionBenchmark()
    {
        var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(ProxyConstructionBenchmark).Assembly))
            .BuildServiceProvider();
        var grainType = GrainType.Create("proxy-benchmark");
        var interfaceType = GrainInterfaceType.Create("proxy-benchmark");
        var codecProvider = services.GetRequiredService<CodecProvider>();
        var copyContextPool = services.GetRequiredService<CopyContextPool>();
        _noCopiers = new OrleansCodeGen.Benchmarks.Serialization.ProxyActivator_IProxyNoCopierGrain(
            grainType, interfaceType, 0, null!, InvokeMethodOptions.None, codecProvider, copyContextPool, services);
        _concrete = new OrleansCodeGen.Benchmarks.Serialization.ProxyActivator_IProxyConcreteCopierGrain(
            grainType, interfaceType, 0, null!, InvokeMethodOptions.None, codecProvider, copyContextPool, services);
        _interface = new OrleansCodeGen.Benchmarks.Serialization.ProxyActivator_IProxyInterfaceCopierGrain(
            grainType, interfaceType, 0, null!, InvokeMethodOptions.None, codecProvider, copyContextPool, services);
        _fourInterfaces = new OrleansCodeGen.Benchmarks.Serialization.ProxyActivator_IProxyFourInterfaceCopiersGrain(
            grainType, interfaceType, 0, null!, InvokeMethodOptions.None, codecProvider, copyContextPool, services);
    }

    [Benchmark(Baseline = true)]
    public object NoCopiers() => _noCopiers.CreateReference(_grainId);

    [Benchmark]
    public object OneConcreteCopier() => _concrete.CreateReference(_grainId);

    [Benchmark]
    public object OneInterfaceCopier() => _interface.CreateReference(_grainId);

    [Benchmark]
    public object FourInterfaceCopiers() => _fourInterfaces.CreateReference(_grainId);
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
