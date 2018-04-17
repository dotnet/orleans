using Orleans.CodeGeneration;
using BenchmarkGrains.MapReduce;

[assembly: GenerateSerializer(typeof(MapProcessor))]
[assembly: GenerateSerializer(typeof(ReduceProcessor))]
[assembly: GenerateSerializer(typeof(EmptyProcessor))]
