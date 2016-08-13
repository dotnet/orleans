using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using BenchmarkDotNet.Attributes;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace SerializationBenchmarks
{
    [Config(typeof(SerializationBenchmarkConfig))]
    public class SerializationBenchmarks
    {
        private PocoState poco;

        [Params(60000)]
        public int Repeats { get; set; }

        [Setup]
        public void BenchmarkSetup()
        {
            poco = new PocoState
            {
                Num1 = 4,
                Num2 = 556321,
                Num3 = 77237,
                Str = "SerializationBenchmarks.DictionaryCreation.Benchmark"
            };

            SerializationManager.InitializeForTesting();

            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
        }

        [Benchmark]
        public void SerializerBenchmark()
        {
            for (int i = 0; i < Repeats; i++)
            {
                var bytes = SerializationManager.SerializeToByteArray(poco);
            }
        }

        [Benchmark]
        public void DeserializerBenchmark()
        {
            var bytes = SerializationManager.SerializeToByteArray(poco);
            for (int i = 0; i < Repeats; i++)
            {
                var obj = SerializationManager.DeserializeFromByteArray<PocoState>(bytes);
            }
        }
    }
}
