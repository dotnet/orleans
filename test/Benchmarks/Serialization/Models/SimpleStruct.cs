using System;
using Orleans;

namespace Benchmarks.Models
{
    [Serializable]
    [GenerateSerializer]
    public struct SimpleStruct
    {
        [Id(0)]
        public int Int { get; set; }

        [Id(1)]
        public bool Bool { get; set; }

        [Id(3)]
        public object AlwaysNull { get; set; }

        [Id(4)]
        public Guid Guid { get; set; }
    }
}