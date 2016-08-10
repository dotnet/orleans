using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Running;
using GrainInterfaces;
using Orleans;
using Orleans.Runtime.Host;
using SerializationBenchmarks.MapReduce;

namespace SerializationBenchmarks
{
  
    class Program
    {
        private static readonly Dictionary<string, Action> _benchmarks = new Dictionary<string, Action>
        {
            ["MapReduce"] = () =>
            {
                Console.WriteLine("Running MapReduce benchmark");
                var mapReduceBenchmark = new MapReduceBenchmark();
                mapReduceBenchmark.BenchmarkSetup();
                var z = Stopwatch.StartNew();
                mapReduceBenchmark.Bench().Wait();
                Console.WriteLine(z.ElapsedMilliseconds);
                Console.ReadLine();
            },
            ["Serialization"] = () =>
            {
                var summary = BenchmarkRunner.Run<SerializationBenchmarks>();
            }
        };

        static void Main(string[] args)
        {
            _benchmarks["MapReduce"]();
            return;
            if (args.Length == 0 || !_benchmarks.ContainsKey(args[0]))
            {
                Console.WriteLine("Running full benchmarks suite");
                _benchmarks.Select(pair => pair.Value).ToList().ForEach(action => action());
            }
            else
            {
                _benchmarks[args[0]]();
            }

            Console.Read();
        }
    }
}
