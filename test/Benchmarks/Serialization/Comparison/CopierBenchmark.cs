using System.Buffers;
using BenchmarkDotNet.Attributes;
using Benchmarks.Models;
using Benchmarks.Serialization.Models;
using Benchmarks.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Benchmarks.Serialization.Comparison;

[Config(typeof(BenchmarkConfig))]
public class CopierBenchmark
{
    private static readonly MyVector3[] _vectorArray;
    private static readonly DeepCopier<MyVector3[]> _arrayCopier;
    private static readonly DeepCopier<IntStruct> _structCopier;
    private static readonly IntStruct _intStruct;
    private static readonly DeepCopier<IntClass> _classCopier;
    private static readonly ImmutableVector3[] _arrayOfImmutableVectors;
    private static readonly IntClass _intClass;
    private static readonly DeepCopier<ImmutableVector3[]> _arrayOfImmutableVectorsCopier;
    private static readonly SerializerSession _session;

    static CopierBenchmark()
    {
        _vectorArray = Enumerable.Repeat(new MyVector3 { X = 10.3f, Y = 40.5f, Z = 13411.3f }, 1000).ToArray();
        _arrayOfImmutableVectors = Enumerable.Repeat(new ImmutableVector3 { X = 10.3f, Y = 40.5f, Z = 13411.3f }, 1000).ToArray();
        var serviceProvider = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(ArraySerializeBenchmark).Assembly))
            .BuildServiceProvider();
        _arrayCopier = serviceProvider.GetRequiredService<DeepCopier<MyVector3[]>>();
        _structCopier = serviceProvider.GetRequiredService<DeepCopier<IntStruct>>();
        _intStruct = IntStruct.Create();
        _classCopier = serviceProvider.GetRequiredService<DeepCopier<IntClass>>();
        _intClass = IntClass.Create();
        _arrayOfImmutableVectorsCopier = serviceProvider.GetRequiredService<DeepCopier<ImmutableVector3[]>>();
        _session = serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();
    }

    [Benchmark]
    public void VectorArray() => _arrayCopier.Copy(_vectorArray);

    [Benchmark]
    public void ImmutableVectorArray() => _arrayOfImmutableVectorsCopier.Copy(_arrayOfImmutableVectors);

    [Benchmark]
    public void Struct() => _structCopier.Copy(_intStruct);

    [Benchmark]
    public void Class() => _classCopier.Copy(_intClass);
}
