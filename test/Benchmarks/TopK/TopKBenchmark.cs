using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime.Placement.Repartitioning;

namespace Benchmarks.TopK;

[MemoryDiagnoser]
public class TopKBenchmark
{
    private ZipfRejectionSampler _sampler;
    private ulong[] ULongSamples;
    private Edge[] EdgeSamples;
    private EdgeClass[] EdgeClassSamples;
    private UlongFrequentItemCollection _fss;
    private EdgeClassFrequentItemCollection _fssClass;
    private EdgeFrequentItemCollection _fssEdge;
    private FrequencySink _sink;

    [Params(100_000, Priority = 3)]
    public int Pop { get; set; }

    [Params(0.2, 0.4, 0.6, 0.8, 1.02, 1.2, 1.4, 1.6, Priority = 2)]
    public double Skew { get; set; }

    [Params(10_000, Priority = 1)]
    public int Cap { get; set; }

    [Params(1_000_000, Priority = 4)]
    public int Samples { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sampler = new(new Random(42), Pop, Skew);

        var silos = new SiloAddress[100];
        for (var i = 0; i < silos.Length; i++)
        {
            silos[i] = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, i), i);
        }

        var grains = new GrainId[Pop];
        for (var i = 0; i < Pop; i++)
        {
            grains[i] = GrainId.Create("grain", i.ToString());
        }

        var grainEdges = new Edge[Pop];
        for (var i = 0; i < Pop; i++)
        {
            grainEdges[i] = new Edge(new(grains[i % grains.Length], silos[i % silos.Length], true), new(grains[(i + 1) % grains.Length], silos[(i + 1) % silos.Length], true));
        }

        var grainEdgeClasses = new EdgeClass[Pop];
        for (var i = 0; i < Pop; i++)
        {
            grainEdgeClasses[i] = new(grainEdges[i]);
        }

        ULongSamples = new ulong[Samples];
        EdgeSamples = new Edge[Samples];
        EdgeClassSamples = new EdgeClass[Samples];
        for (var i = 0; i < Samples; i++)
        {
            var sample = _sampler.Sample();
            ULongSamples[i] = (ulong)sample;
            EdgeSamples[i] = grainEdges[sample % grainEdges.Length];
            EdgeClassSamples[i] = grainEdgeClasses[sample % grainEdgeClasses.Length];
        }

        _fss = new UlongFrequentItemCollection(Cap);
        _fssClass = new EdgeClassFrequentItemCollection(Cap);
        _fssEdge = new EdgeFrequentItemCollection(Cap);
        _sink = new FrequencySink(Cap);
    }

    internal sealed record class EdgeClass(Edge Edge);

    [IterationSetup]
    public void IterationSetup()
    {
        /*
        _fss.Clear();
        _fssEdge.Clear();
        _fssClass.Clear();
        */
        //_sink = new FrequencySink(Cap);
    }

    /*
    [Benchmark]
    [BenchmarkCategory("Add")]
    public void FssULongAdd()
    {
        foreach (var sample in ULongSamples)
        {
            _fss.Add(sample);
        }
    }
    */

    /*
    [Benchmark]
    [BenchmarkCategory("Add")]
    public void FssClassAdd()
    {
        foreach (var sample in EdgeClassSamples)
        {
            _fssClass.Add(sample);
        }
    }
    */

    [Benchmark]
    [BenchmarkCategory("FSS")]
    public void FssAdd()
    {
        foreach (var sample in EdgeSamples)
        {
            _fssEdge.Add(sample);
        }
    }

    [Benchmark]
    [BenchmarkCategory("SS")]
    public void SinkAdd()
    {
        foreach (var sample in EdgeSamples)
        {
            _sink.Add(sample);
        }
    }

    private sealed class EdgeFrequentItemCollection(int capacity) : FrequentItemCollection<ulong, Edge>(capacity)
    {
        protected override ulong GetKey(in Edge element) => (ulong)element.Source.Id.GetUniformHashCode() << 32 | element.Target.Id.GetUniformHashCode();
        public void Clear() => ClearCore();
    }

    private sealed class EdgeClassFrequentItemCollection(int capacity) : FrequentItemCollection<ulong, EdgeClass>(capacity)
    {
        static ulong GetKey(in Edge element) => (ulong)element.Source.Id.GetUniformHashCode() << 32 | element.Target.Id.GetUniformHashCode();
        protected override ulong GetKey(in EdgeClass element) => GetKey(element.Edge);
        public void Clear() => ClearCore();
    }

    private sealed class UlongFrequentItemCollection(int capacity) : FrequentItemCollection<ulong, ulong>(capacity)
    {
        protected override ulong GetKey(in ulong element) => element;
        public void Remove(in ulong element) => Remove(GetKey(element));
        public void Clear() => ClearCore();
    }

    internal class EdgeCounter(ulong value, Edge edge)
    {
        public ulong Value { get; set; } = value;
        public Edge Edge { get; } = edge;

    }

    /// <summary>
    /// Implementation of the Space-Saving algorithm: https://www.cse.ust.hk/~raywong/comp5331/References/EfficientComputationOfFrequentAndTop-kElementsInDataStreams.pdf
    /// </summary>
    internal sealed class FrequencySink(int capacity)
    {
        public ulong GetKey(in Edge element) => (ulong)element.Source.Id.GetUniformHashCode() << 32 | element.Target.Id.GetUniformHashCode();
        private readonly Dictionary<ulong, EdgeCounter> _counters = new(capacity);
        private readonly UpdateableMinHeap _heap = new(capacity);

        public int Capacity { get; } = capacity;
        public Dictionary<ulong, EdgeCounter>.ValueCollection Counters => _counters.Values;

        public void Add(Edge edge)
        {
            var combinedHash = GetKey(edge);
            if (_counters.TryGetValue(combinedHash, out var counter))
            {
                counter.Value++;
                _heap.Update(combinedHash, counter.Value);

                return;
            }

            if (_counters.Count == Capacity)
            {
                var minHash = _heap.Dequeue();
                _counters.Remove(minHash);
            }

            _counters.Add(combinedHash, new EdgeCounter(1, edge));
            _heap.Enqueue(combinedHash, _counters[combinedHash].Value);
        }

        public void Remove(uint sourceHash, uint targetHash)
        {
            var combinedHash = CombineHashes(sourceHash, targetHash);
            var reversedHash = CombineHashes(targetHash, sourceHash);

            if (_counters.Remove(combinedHash))
            {
                _ = _heap.Remove(combinedHash);
            }

            if (_counters.Remove(reversedHash))
            {
                _ = _heap.Remove(reversedHash);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong CombineHashes(uint sourceHash, uint targetHash)
            => (ulong)sourceHash << 32 | targetHash;

        // Inspired by: https://github.com/DesignEngrLab/TVGL/blob/master/TessellationAndVoxelizationGeometryLibrary/Miscellaneous%20Functions/UpdatablePriorityQueue.cs
        private class UpdateableMinHeap(int capacity)
        {
            private const int Arity = 4;
            private const int Log2Arity = 2;

            private readonly Dictionary<ulong, int> _hashIndexes = new(capacity);
            private readonly (ulong Hash, ulong Value)[] _nodes = new (ulong, ulong)[capacity];

            private int _size;

            public void Enqueue(ulong hash, ulong value)
            {
                var currentSize = _size;
                _size = currentSize + 1;

                MoveNodeUp((hash, value), currentSize);
            }

            public ulong Dequeue()
            {
                var hash = _nodes[0].Hash;
                _hashIndexes.Remove(hash);

                var lastNodeIndex = --_size;
                if (lastNodeIndex > 0)
                {
                    var lastNode = _nodes[lastNodeIndex];
                    MoveNodeDown(lastNode, 0);
                }

                return hash;
            }

            public bool Remove(ulong hash)
            {
                if (!_hashIndexes.TryGetValue(hash, out var index))
                {
                    return false;
                }

                var nodes = _nodes;
                var newSize = --_size;

                if (index < newSize)
                {
                    var lastNode = nodes[newSize];
                    MoveNodeDown(lastNode, index);
                }

                _hashIndexes.Remove(hash);
                nodes[newSize] = default;

                return true;
            }

            public void Update(ulong hash, ulong newValue)
            {
                Remove(hash);
                Enqueue(hash, newValue);
            }

            private void MoveNodeUp((ulong Hash, ulong Value) node, int nodeIndex)
            {
                Debug.Assert(0 <= nodeIndex && nodeIndex < _size);

                var nodes = _nodes;

                while (nodeIndex > 0)
                {
                    var parentIndex = GetParentIndex(nodeIndex);
                    var parent = nodes[parentIndex];

                    if (Comparer<ulong>.Default.Compare(node.Value, parent.Value) < 0)
                    {
                        nodes[nodeIndex] = parent;
                        _hashIndexes[parent.Hash] = nodeIndex;
                        nodeIndex = parentIndex;
                    }
                    else
                    {
                        break;
                    }
                }

                _hashIndexes[node.Hash] = nodeIndex;
                nodes[nodeIndex] = node;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static int GetParentIndex(int index) => (index - 1) >> Log2Arity;
            }

            private void MoveNodeDown((ulong Hash, ulong Value) node, int nodeIndex)
            {
                Debug.Assert(0 <= nodeIndex && nodeIndex < _size);

                var nodes = _nodes;
                var size = _size;

                int i;
                while ((i = GetFirstChildIndex(nodeIndex)) < size)
                {
                    var minChild = nodes[i];
                    var minChildIndex = i;

                    var childIndexUpperBound = Math.Min(i + Arity, size);
                    while (++i < childIndexUpperBound)
                    {
                        var nextChild = nodes[i];
                        if (nextChild.Value < minChild.Value)
                        {
                            minChild = nextChild;
                            minChildIndex = i;
                        }
                    }

                    if (node.Value <= minChild.Value)
                    {
                        break;
                    }

                    nodes[nodeIndex] = minChild;
                    _hashIndexes[minChild.Hash] = nodeIndex;
                    nodeIndex = minChildIndex;
                }

                _hashIndexes[node.Hash] = nodeIndex;
                nodes[nodeIndex] = node;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static int GetFirstChildIndex(int index) => (index << Log2Arity) + 1;
            }
        }
    }
}

    // https://jasoncrease.medium.com/rejection-sampling-the-zipf-distribution-6b359792cffa
    internal sealed class ZipfRejectionSampler
    {
        private readonly Random _rand;
        private readonly double _skew;
        private readonly double _t;

        public ZipfRejectionSampler(Random random, long cardinality, double skew)
        {
            _rand = random;
            _skew = skew;
            _t = (Math.Pow(cardinality, 1 - skew) - skew) / (1 - skew);
        }

        public long Sample()
        {
            while (true)
            {
                double invB = bInvCdf(_rand.NextDouble());
                long sampleX = (long)(invB + 1);
                double yRand = _rand.NextDouble();
                double ratioTop = Math.Pow(sampleX, -_skew);
                double ratioBottom = sampleX <= 1 ? 1 / _t : Math.Pow(invB, -_skew) / _t;
                double rat = (ratioTop) / (ratioBottom * _t);

                if (yRand < rat)
                    return sampleX;
            }
        }
        private double bInvCdf(double p)
        {
            if (p * _t <= 1)
                return p * _t;
            else
                return Math.Pow((p * _t) * (1 - _skew) + _skew, 1 / (1 - _skew));
        }
    }
