using System;
using System.Collections.Generic;
using System.Linq;
using OrleansGrainInterfaces.MapReduce;

namespace OrleansBenchmarkGrains.MapReduce
{
    [Serializable]
    public class MapProcessor : ITransformProcessor<string, List<string>>
    {
        private static readonly char[] _delimiters = { '.', '?', '!', ' ', ';', ':', ',' };

        public List<string> Process(string input)
        {
            return input
                 .Split(_delimiters, StringSplitOptions.RemoveEmptyEntries)
                 .ToList();
        }
    }

    [Serializable]
    public class ReduceProcessor : ITransformProcessor<List<string>, Dictionary<string, int>>
    {
        public Dictionary<string, int> Process(List<string> input)
        {
            return input.GroupBy(v => v.ToLowerInvariant()).Select(v => new
            {
                key = v.Key,
                count = v.Count()
            }).ToDictionary(arg => arg.key, arg => arg.count);
        }
    }

    [Serializable]
    public class EmptyProcessor : ITransformProcessor<Dictionary<string, int>, Dictionary<string, int>>
    {
        public Dictionary<string, int> Process(Dictionary<string, int> input)
        {
            return input;
        }
    }
}