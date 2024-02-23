using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Placement.Rebalancing;

namespace Orleans.Runtime.Placement.Rebalancing;

#nullable enable

/// <summary>
/// Implementation of the Space-Saving algorithm: https://www.cse.ust.hk/~raywong/comp5331/References/EfficientComputationOfFrequentAndTop-kElementsInDataStreams.pdf
/// </summary>
internal sealed class FrequencySink(int capacity)
{
    private readonly Dictionary<ulong, EdgeCounter> _counters = new(capacity);
    private readonly UpdateableMinHeap _heap = new(capacity);

    public int Capacity { get; } = capacity;
    public IReadOnlyCollection<EdgeCounter> Counters => _counters.Values;

    public void Add(CommEdge edge)
    {
        var sourceHash = edge.Source.Id.GetUniformHashCode();
        var targetHash = edge.Target.Id.GetUniformHashCode();

        // Preserves all bits, so we lower chances of collisions and we can differentiate vertices of type 'A -> B' from 'B -> A',
        // which is important when A and B are in different silos, and the edge strengths differ.
        // For example: A -> B once every second, whereas B -> A happens twice every second.
        var combinedHash = CombineHashes(sourceHash, targetHash);

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

        private readonly Dictionary<ulong, int> _hashIndeces = new(capacity);
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
            _hashIndeces.Remove(hash);

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
            if (!_hashIndeces.TryGetValue(hash, out var index))
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

            _hashIndeces.Remove(hash);
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
                    _hashIndeces[parent.Hash] = nodeIndex;
                    nodeIndex = parentIndex;
                }
                else
                {
                    break;
                }
            }

            _hashIndeces[node.Hash] = nodeIndex;
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
                    if (Comparer<ulong>.Default.Compare(nextChild.Value, minChild.Value) < 0)
                    {
                        minChild = nextChild;
                        minChildIndex = i;
                    }
                }

                if (Comparer<ulong>.Default.Compare(node.Value, minChild.Value) <= 0)
                {
                    break;
                }

                nodes[nodeIndex] = minChild;
                _hashIndeces[minChild.Hash] = nodeIndex;
                nodeIndex = minChildIndex;
            }

            _hashIndeces[node.Hash] = nodeIndex;
            nodes[nodeIndex] = node;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static int GetFirstChildIndex(int index) => (index << Log2Arity) + 1;
        }
    }
}