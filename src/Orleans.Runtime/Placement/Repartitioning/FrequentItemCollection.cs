#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Orleans.Placement.Repartitioning;

namespace Orleans.Runtime.Placement.Repartitioning;

internal sealed class FrequentEdgeCounter(int capacity) : FrequentItemCollection<ulong, Edge>(capacity)
{
    protected override ulong GetKey(in Edge element) => ((ulong)element.Source.Id.GetUniformHashCode()) << 32 | element.Target.Id.GetUniformHashCode();
    public void Clear() => ClearCore();
    public void Remove(in Edge element) => RemoveCore(GetKey(element));
}

// This is Implementation of "Filtered Space-Saving" from "Finding top-k elements in data streams"
// by Nuno Homem &amp; Joao Paulo Carvalho (https://www.hlt.inesc-id.pt/~fmmb/references/misnis.ref0a.pdf). 
// In turn, this is a modification of the "Space-Saving" algorithm by Metwally, Agrawal, and Abbadi,
// Described in "Efficient Computation of Frequent and Top-k Elements in Data Streams" (https://www.cs.emory.edu/~cheung/Courses/584/Syllabus/papers/Frequency-count/2005-Metwally-Top-k-elements.pdf).
// This is implemented using an in-lined version of .NET's PriorityQueue which has been modified
// to support incrementing a value and with an index mapping key hashes to heap indexes.
internal abstract class FrequentItemCollection<TKey, TElement>(int capacity) where TElement : notnull where TKey : notnull
{
    /// <summary>
    /// Represents an implicit heap-ordered complete d-ary tree, stored as an array.
    /// </summary>
    private Counter[] _heap = [];

    /// <summary>
    /// A dictionary that maps the hash of a key to its index in the heap.
    /// </summary>
    private readonly Dictionary<TKey, int> _heapIndex = [];

    /// <summary>
    /// The number of nodes in the heap.
    /// </summary>
    private int _heapSize;

    /// <summary>
    /// Specifies the arity of the d-ary heap, which here is quaternary.
    /// It is assumed that this value is a power of 2.
    /// </summary>
    private const int Arity = 4;

    /// <summary>
    /// The binary logarithm of <see cref="Arity" />.
    /// </summary>
    private const int Log2Arity = 2;

    /// <summary>
    /// Contains count estimates for keys that are not being tracked, indexed by the hash of the key.
    /// Collisions are expected.
    /// </summary>
    private readonly uint[] _sketch = new uint[GetSketchSize(capacity)];

    /// <summary>
    /// Gets the number of elements contained in the <see cref="FrequentItemCollection{TKey,TElement}"/>.
    /// </summary>
    public int Count => _heapSize;

    /// <summary>
    /// Gets the number of elements which the <see cref="FrequentItemCollection{TKey, TElement}"/> will track.
    /// </summary>
    public int Capacity { get; } = capacity;

#if DEBUG
    static FrequentItemCollection()
    {
        Debug.Assert(Log2Arity > 0 && Math.Pow(2, Log2Arity) == Arity);
    }
#endif

    /// <summary>
    /// Returns a collection of up to <see cref="Capacity"/> keys, along with their count estimates, in unspecified order.
    /// </summary>
    public ElementEnumerator Elements => new(this);

    protected abstract TKey GetKey(in TElement element);

    public void Add(in TElement element)
    {
        const int Increment = 1;
        var nodeIndexHash = GetKey(element);

        // Increase count of a key that is already being tracked.
        // There is a minute chance of a hash collision, which is deemed acceptable and ignored.
        if (_heapIndex.TryGetValue(nodeIndexHash, out var index))
        {
            ref var counter = ref _heap[index];
            counter.Count += Increment;
            MoveUpHeap(counter, index, nodeIndexHash);
            return;
        }

        // Key is not being tracked, but can fit in the top K, so add it.
        if (Count < Capacity)
        {
            InsertHeap(new Counter(element, Increment, error: 0), nodeIndexHash);
            return;
        }

        var min = _heap[0];

        // Filter out values which are estimated to have appeared less frequently than the minimum.
        var sketchMask = _sketch.Length - 1;
        var sketchHash = nodeIndexHash.GetHashCode();
        var countEstimate = _sketch[sketchHash & sketchMask];
        if (countEstimate + Increment < min.Count)
        {
            // Increase the count estimate.
            _sketch[sketchHash & sketchMask] += Increment;
            return;
        }

        // Remove the minimum element from the hash index.
        var minIndexHash = GetKey(min.Element);
        _heapIndex.Remove(minIndexHash);

        // While evicting the minimum element, update its counter in the sketch to improve the chance of it
        // passing the filter in the future.
        var minHash = minIndexHash.GetHashCode();
        _sketch[minHash & sketchMask] = min.Count;

        // Push the new element in place of the last and move it down until it's in position.
        MoveDownHeap(new Counter(element, countEstimate + Increment, error: countEstimate), 0, nodeIndexHash);
    }

    /// <summary>
    /// Removes the counter corresponding to the specified hash.
    /// </summary>
    /// <param name="key">The key of the value to remove.</param>
    /// <returns><see langword="true"/> if matching entry was found and removed, <see langword="false"/> otherwise.</returns>
    protected bool RemoveCore(TKey key)
    {
        // Remove the element from the sketch
        var sketchMask = _sketch.Length - 1;
        var sketchHash = key.GetHashCode();
        _sketch[sketchHash & sketchMask] = 0;

        // Remove the element from the heap index
        if (!_heapIndex.Remove(key, out var index))
        {
            return false;
        }

        // Remove the element from the heap
        var nodes = _heap;
        var newSize = --_heapSize;
        if (index < newSize)
        {
            // We're removing an element from the middle of the heap.
            // Pop the last element in the collection and sift downward from the removed index.
            var lastNode = nodes[newSize];

            MoveDownHeap(lastNode, index, GetKey(lastNode.Element));
        }

        nodes[newSize] = default;

        return true;
    }

    protected void ClearCore()
    {
        Array.Clear(_heap, 0, _heapSize);
        _heapIndex.Clear();
        Array.Clear(_sketch);
        _heapSize = 0;
    }

    private static int GetSketchSize(int capacity)
    {
        // Suggested constants in the paper "Finding top-k elements in data streams", chap 6. equation (24)
        // Round to nearest power of 2 for cheaper binning without modulo
        const int SketchEntriesPerHeapEntry = 6;

        return 1 << 32 - int.LeadingZeroCount(capacity * SketchEntriesPerHeapEntry);
    }

    /// <summary>
    ///  Adds the specified element to the <see cref="FrequentItemCollection{TKey, TElement}"/>.
    /// </summary>
    /// <param name="element">The element to add.</param>
    private void InsertHeap(Counter element, TKey key)
    {
        // Virtually add the node at the end of the underlying array.
        // Note that the node being enqueued does not need to be physically placed
        // there at this point, as such an assignment would be redundant.

        var currentSize = _heapSize;

        if (_heap.Length == currentSize)
        {
            GrowHeap(currentSize + 1);
        }

        _heapSize = currentSize + 1;

        MoveUpHeap(element, currentSize, key);
    }

    /// <summary>
    /// Grows the priority queue to match the specified min capacity.
    /// </summary>
    private void GrowHeap(int minCapacity)
    {
        Debug.Assert(_heap.Length < minCapacity);

        const int GrowFactor = 2;
        const int MinimumGrow = 4;

        var newCapacity = GrowFactor * _heap.Length;

        // Allow the queue to grow to maximum possible capacity (~2G elements) before encountering overflow.
        // Note that this check works even when _nodes.Length overflowed thanks to the (uint) cast
        if ((uint)newCapacity > Array.MaxLength) newCapacity = Array.MaxLength;

        // Ensure minimum growth is respected.
        newCapacity = Math.Max(newCapacity, _heap.Length + MinimumGrow);

        // If the computed capacity is still less than specified, set to the original argument.
        // Capacities exceeding Array.MaxLength will be surfaced as OutOfMemoryException by Array.Resize.
        if (newCapacity < minCapacity) newCapacity = minCapacity;

        Array.Resize(ref _heap, newCapacity);
    }

    /// <summary>
    /// Gets the index of an element's parent.
    /// </summary>
    private static int GetParentIndex(int index) => index - 1 >> Log2Arity;

    /// <summary>
    /// Gets the index of the first child of an element.
    /// </summary>
    private static int GetFirstChildIndex(int index) => (index << Log2Arity) + 1;

    /// <summary>
    /// Moves a node up in the tree to restore heap order.
    /// </summary>
    private void MoveUpHeap(Counter node, int nodeIndex, TKey nodeKey)
    {
        // Instead of swapping items all the way to the root, we will perform
        // a similar optimization as in the insertion sort.

        Debug.Assert(0 <= nodeIndex && nodeIndex < _heapSize);

        var nodes = _heap;
        var hashIndex = _heapIndex;

        while (nodeIndex > 0)
        {
            var parentIndex = GetParentIndex(nodeIndex);
            var parent = nodes[parentIndex];

            if (node.CompareTo(parent) < 0)
            {
                nodes[nodeIndex] = parent;
                hashIndex[GetKey(parent.Element)] = nodeIndex;
                nodeIndex = parentIndex;
            }
            else
            {
                break;
            }
        }

        nodes[nodeIndex] = node;
        hashIndex[nodeKey] = nodeIndex;
    }

    /// <summary>
    /// Moves a node down in the tree to restore heap order.
    /// </summary>
    private void MoveDownHeap(Counter node, int nodeIndex, TKey nodeKey)
    {
        // The node to move down will not actually be swapped every time.
        // Rather, values on the affected path will be moved up, thus leaving a free spot
        // for this value to drop in. Similar optimization as in the insertion sort.

        Debug.Assert(0 <= nodeIndex && nodeIndex < _heapSize);

        var nodes = _heap;
        var size = _heapSize;
        var hashIndex = _heapIndex;

        int i;
        while ((i = GetFirstChildIndex(nodeIndex)) < size)
        {
            // Find the child node with the minimal priority
            var minChild = nodes[i];
            var minChildIndex = i;

            var childIndexUpperBound = Math.Min(i + Arity, size);
            while (++i < childIndexUpperBound)
            {
                var nextChild = nodes[i];
                if (nextChild.CompareTo(minChild) < 0)
                {
                    minChild = nextChild;
                    minChildIndex = i;
                }
            }

            // Heap property is satisfied; insert node in this location.
            if (node.CompareTo(minChild) <= 0)
            {
                break;
            }

            // Move the minimal child up by one node and continue recursively from its location.
            nodes[nodeIndex] = minChild;
            hashIndex[GetKey(minChild.Element)] = nodeIndex;
            nodeIndex = minChildIndex;
        }

        hashIndex[nodeKey] = nodeIndex;
        nodes[nodeIndex] = node;
    }

    private struct Counter(TElement element, uint count, uint error) : IComparable<Counter>
    {
        public readonly TElement Element = element;
        public uint Count = count;
        public uint Error = error;

        public readonly int CompareTo(Counter other) => ((ulong)Count << 32 | uint.MaxValue - Error).CompareTo((ulong)other.Count << 32 | uint.MaxValue - other.Error);

        public override readonly string ToString() => $"{Element}: Count: {Count} Error: {Error}";
    }

    /// <summary>
    ///  Enumerates the element and priority pairs of a <see cref="FrequentItemCollection{TKey, TElement}"/>,
    ///  without any ordering guarantees.
    /// </summary>
    public struct ElementEnumerator : IEnumerator<(TElement Element, uint Count, uint Error)>, IEnumerable<(TElement Element, uint Count, uint Error)>
    {
        private readonly FrequentItemCollection<TKey, TElement> _heap;
        private int _index;
        private Counter _current;

        internal ElementEnumerator(FrequentItemCollection<TKey, TElement> heap)
        {
            _heap = heap;
            _index = 0;
            _current = default;
        }

        /// <summary>
        /// Releases all resources used by the <see cref="ElementEnumerator"/>.
        /// </summary>
        public readonly void Dispose() { }

        /// <summary>
        /// Advances the enumerator to the next element of the heap.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            var localHeap = _heap;

            if ((uint)_index < (uint)localHeap._heapSize)
            {
                _current = localHeap._heap[_index];
                _index++;
                return true;
            }

            return MoveNextRare();
        }

        private bool MoveNextRare()
        {
            _index = _heap._heapSize + 1;
            _current = default;
            return false;
        }

        /// <summary>
        /// Gets the element at the current position of the enumerator.
        /// </summary>
        public readonly (TElement Element, uint Count, uint Error) Current => (_current.Element, _current.Count, _current.Error);

        readonly object IEnumerator.Current => _current;

        void IEnumerator.Reset()
        {
            _index = 0;
            _current = default;
        }

        public readonly ElementEnumerator GetEnumerator() => this;
        readonly IEnumerator<(TElement Element, uint Count, uint Error)> IEnumerable<(TElement Element, uint Count, uint Error)>.GetEnumerator() => this;
        readonly IEnumerator IEnumerable.GetEnumerator() => this;
    }
}
