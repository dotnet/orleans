using System;
using Orleans;

namespace Benchmarks.Models
{
    [Serializable]
    [GenerateSerializer]
    public class SimpleClass
    {
        [Id(0)]
        public int BaseInt { get; set; }
    }
}