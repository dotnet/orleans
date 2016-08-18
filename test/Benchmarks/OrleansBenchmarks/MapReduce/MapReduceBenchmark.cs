using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using OrleansBenchmarkGrains.MapReduce;
using OrleansGrainInterfaces.MapReduce;

namespace OrleansBenchmarks.MapReduce
{
    public class MapReduceBenchmark
    {
        private static TestCluster _host;
        private readonly int _intermediateStagesCount = 15;
        private readonly int _pipelineParallelization = 4;
        private readonly int _repeats = 50000;
        private int _currentRepeat = 0;

        [Setup]
        public void BenchmarkSetup()
        {
            TestClusterOptions.DefaultTraceToConsole = false;
            var options = new TestClusterOptions(1);
            options.ClusterConfiguration.ApplyToAllNodes(c => c.DefaultTraceLevel = Severity.Warning);
            _host = new TestCluster(options);
            _host.Deploy();
        }

        [Benchmark]
        public async Task Bench()
        {
            var pipelines = Enumerable
                .Range(0, _pipelineParallelization)
                .AsParallel()
                .WithDegreeOfParallelism(4)
                .Select(async i =>
                {
                    await BenchCore();
                });

            await Task.WhenAll(pipelines);
        }

        public void Teardown()
        {
            _host.StopAllSilos();
        }

        private async Task BenchCore()
        {
            List<Task> initializationTasks = new List<Task>();
            var mapper = GrainClient.GrainFactory.GetGrain<ITransformGrain<string, List<string>>>(Guid.NewGuid());
            initializationTasks.Add(mapper.Initialize(new MapProcessor()));
            var reducer =
                GrainClient.GrainFactory.GetGrain<ITransformGrain<List<string>, Dictionary<string, int>>>(Guid.NewGuid());
            initializationTasks.Add(reducer.Initialize(new ReduceProcessor()));

            // used for imitation of complex processing pipelines
            var intermediateGrains = Enumerable
                .Range(0, _intermediateStagesCount)
                .Select(i =>
                {
                    var intermediateProcessor =
                        GrainClient.GrainFactory.GetGrain<ITransformGrain<Dictionary<string, int>, Dictionary<string, int>>>
                            (Guid.NewGuid());
                    initializationTasks.Add(intermediateProcessor.Initialize(new EmptyProcessor()));
                    return intermediateProcessor;
                });

            initializationTasks.Add(mapper.LinkTo(reducer));
            var collector = GrainClient.GrainFactory.GetGrain<IBufferGrain<Dictionary<string, int>>>(Guid.NewGuid());
            using (var e = intermediateGrains.GetEnumerator())
            {
                ITransformGrain<Dictionary<string, int>, Dictionary<string, int>> previous = null;
                if (e.MoveNext())
                {
                    initializationTasks.Add(reducer.LinkTo(e.Current));
                    previous = e.Current;
                }

                while (e.MoveNext())
                {
                    initializationTasks.Add(previous.LinkTo(e.Current));
                    previous = e.Current;
                }

                initializationTasks.Add(previous.LinkTo(collector));
            }

            await Task.WhenAll(initializationTasks);

            List<Dictionary<string, int>> resultList = new List<Dictionary<string, int>>();

            while (Interlocked.Increment(ref _currentRepeat) < _repeats)
            {
                await mapper.SendAsync(_text);
                while (!resultList.Any() || resultList.First().Count < 84) // rough way of checking of pipeline completition.
                {
                    resultList = await collector.ReceiveAll();
                }
            }
        }

        private string _text = @"Historically, the world of data and the world of objects" +
          @" have not been well integrated. Programmers work in C# or Visual Basic" +
          @" and also in SQL or XQuery. On the one side are concepts such as classes," +
          @" objects, fields, inheritance, and .NET Framework APIs. On the other side" +
          @" are tables, columns, rows, nodes, and separate languages for dealing with" +
          @" them. Data types often require translation between the two worlds; there are" +
          @" different standard functions. Because the object world has no notion of query, a" +
          @" query can only be represented as a string without compile-time type checking or" +
          @" IntelliSense support in the IDE. Transferring data from SQL tables or XML trees to" +
          @" objects in memory is often tedious and error-prone. Historically, the world of data and the world of objects" +
          @" have not been well integrated. Programmers work in C# or Visual Basic" +
          @" and also in SQL or XQuery. On the one side are concepts such as classes," +
          @" objects, fields, inheritance, and .NET Framework APIs. On the other side" +
          @" are tables, columns, rows, nodes, and separate languages for dealing with" +
          @" them. Data types often require translation between the two worlds; there are" +
          @" different standard functions. Because the object world has no notion of query, a" +
          @" query can only be represented as a string without compile-time type checking or" +
          @" IntelliSense support in the IDE. Transferring data from SQL tables or XML trees to" +
          @" objects in memory is often tedious and error-prone.";
    }

}