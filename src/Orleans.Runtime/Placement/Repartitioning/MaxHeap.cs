#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Placement.Repartitioning;

namespace Orleans.Runtime.Placement.Repartitioning;

internal enum VertexLocation
{
    Unknown,
    Local,
    Remote
}

[DebuggerDisplay("{Vertex} @ {Location}")]
internal sealed class CandidateVertexHeapElement(CandidateVertex value) : IHeapElement<CandidateVertexHeapElement>
{
    public CandidateVertex Vertex { get; } = value;
    public List<(CandidateVertexHeapElement Element, long TransferScore)> ConnectedVertices { get; } = [];
    public GrainId Id => Vertex.Id;
    public long AccumulatedTransferScore { get => Vertex.AccumulatedTransferScore; set => Vertex.AccumulatedTransferScore = value; }
    public VertexLocation Location { get; set; }
    int IHeapElement<CandidateVertexHeapElement>.HeapIndex { get; set; } = -1;
    int IHeapElement<CandidateVertexHeapElement>.CompareTo(CandidateVertexHeapElement other) => AccumulatedTransferScore.CompareTo(other.AccumulatedTransferScore);
}

internal interface IHeapElement<TElement> where TElement : notnull
{
    int HeapIndex { get; set; }
    int CompareTo(TElement other);
}

/// <summary>
///  Represents a max heap.
/// </summary>
/// <typeparam name="TElement">Specifies the type of elements in the heap.</typeparam>
/// <remarks>
///  Implements an array-backed quaternary max-heap.
///  Elements with the lowest priority get removed first.
///  Note: this is based on .NET's PriorityQueue: https://github.com/dotnet/runtime/blob/e78b72b1fdf43d9678877400bcfe801b38c14681/src/libraries/System.Collections/src/System/Collections/Generic/PriorityQueue.cs
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
internal sealed class MaxHeap<TElement> where TElement : notnull, IHeapElement<TElement>
{
    /// <summary>
    /// Represents an implicit heap-ordered complete d-ary tree, stored as an array.
    /// </summary>
    private readonly TElement?[] _nodes;

    /// <summary>
    /// The number of nodes in the heap.
    /// </summary>
    private int _size;

    /// <summary>
    /// Specifies the arity of the d-ary heap, which here is quaternary.
    /// It is assumed that this value is a power of 2.
    /// </summary>
    private const int Arity = 4;

    /// <summary>
    /// The binary logarithm of <see cref="Arity" />.
    /// </summary>
    private const int Log2Arity = 2;

#if DEBUG
    static MaxHeap()
    {
        Debug.Assert(Log2Arity > 0 && Math.Pow(2, Log2Arity) == Arity);
    }
#endif

    /// <summary>
    ///  Initializes a new instance of the <see cref="MaxHeap{TElement}"/> class
    ///  that is populated with the specified elements and priorities.
    /// </summary>
    /// <param name="items">The pairs of elements and priorities with which to populate the queue.</param>
    /// <exception cref="ArgumentNullException">
    ///  The specified <paramref name="items"/> argument was <see langword="null"/>.
    /// </exception>
    /// <remarks>
    ///  Constructs the heap using a heapify operation,
    ///  which is generally faster than enqueuing individual elements sequentially.
    /// </remarks>
    public MaxHeap(List<TElement> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _size = items.Count;
        var nodes = new TElement[_size];

        var i = 0;
        foreach (var item in items)
        {
            nodes[i] = item;
            Debug.Assert(item.HeapIndex == -1);
            item.HeapIndex = i;
            i++;
        }

        _nodes = nodes;
        if (_size > 1)
        {
            Heapify();
        }
        else if (_size == 1)
        {
            _nodes[0]!.HeapIndex = 0;
        }
    }

    /// <summary>
    ///  Gets the number of elements contained in the <see cref="MaxHeap{TElement}"/>.
    /// </summary>
    public int Count => _size;

    public TElement? FirstOrDefault() => _size > 0 ? _nodes[0] : default;

    public bool TryPeek([NotNullWhen(true)] out TElement value)
    {
        if (_size > 0)
        {
            value = _nodes[0]!;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    ///  Returns the maximal element from the <see cref="MaxHeap{TElement}"/> without removing it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The <see cref="MaxHeap{TElement}"/> is empty.</exception>
    /// <returns>The maximal element of the <see cref="MaxHeap{TElement}"/>.</returns>
    public TElement Peek()
    {
        if (_size == 0)
        {
            throw new InvalidOperationException("Collection is empty.");
        }

        return _nodes[0]!;
    }

    public bool TryPop([NotNullWhen(true)] out TElement value)
    {
        if (_size > 0)
        {
            value = Pop();
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    ///  Removes and returns the maximal element from the <see cref="MaxHeap{TElement}"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The queue is empty.</exception>
    /// <returns>The maximal element of the <see cref="MaxHeap{TElement}"/>.</returns>
    public TElement Pop()
    {
        if (_size == 0)
        {
            throw new InvalidOperationException("Collection is empty.");
        }

        var element = _nodes[0]!;
        RemoveRootNode();
        element.HeapIndex = -1;
        return element;

        void RemoveRootNode()
        {
            var lastNodeIndex = --_size;

            if (lastNodeIndex > 0)
            {
                var lastNode = _nodes[lastNodeIndex]!;
                MoveDown(lastNode, 0);
            }

            if (RuntimeHelpers.IsReferenceOrContainsReferences<TElement>())
            {
                _nodes[lastNodeIndex] = default!;
            }
        }
    }

    /// <summary>
    /// Gets the index of an element's parent.
    /// </summary>
    private static int GetParentIndex(int index) => (index - 1) >> Log2Arity;

    /// <summary>
    /// Gets the index of the first child of an element.
    /// </summary>
    private static int GetFirstChildIndex(int index) => (index << Log2Arity) + 1;

    public void OnDecreaseElementPriority(TElement element)
    {
        // If the element has already been removed from the heap, this is a no-op.
        if (element.HeapIndex < 0)
        {
            return;
        }

        // The element's priority has decreased, so move it down as necessary to restore the heap property.
        MoveDown(element, element.HeapIndex);
    }

    public void OnIncreaseElementPriority(TElement element)
    {
        // If the element has already been removed from the heap, this is a no-op.
        if (element.HeapIndex <= 0)
        {
            return;
        }

        // The element's priority has increased, so move it down as necessary to restore the heap property.
        MoveUp(element, element.HeapIndex);
    }

    /// <summary>
    /// Converts an unordered list into a heap.
    /// </summary>
    public void Heapify()
    {
        // Leaves of the tree are in fact 1-element heaps, for which there
        // is no need to correct them. The heap property needs to be restored
        // only for higher nodes, starting from the first node that has children.
        // It is the parent of the very last element in the array.

        var nodes = _nodes;
        var lastParentWithChildren = GetParentIndex(_size - 1);
        for (var index = lastParentWithChildren; index >= 0; --index)
        {
            MoveDown(nodes[index]!, index);
        }
    }

    /// <summary>
    /// Gets the elements in this collection with specified order.
    /// </summary>
    public UnorderedElementEnumerable UnorderedElements => new(this);

    /// <summary>
    /// Moves a node up in the tree to restore heap order.
    /// </summary>
    private void MoveUp(TElement node, int nodeIndex)
    {
        Debug.Assert(0 <= nodeIndex && nodeIndex < _size);

        var nodes = _nodes;

        while (nodeIndex > 0)
        {
            var parentIndex = GetParentIndex(nodeIndex);
            var parentNode = nodes[parentIndex]!;

            if (node.CompareTo(parentNode) <= 0)
            {
                // The parent is more larger than the current node.
                break;
            }

            nodes[nodeIndex] = parentNode;
            parentNode.HeapIndex = nodeIndex;
            nodeIndex = parentIndex;
        }

        nodes[nodeIndex] = node;
        node.HeapIndex = nodeIndex;
    }

    /// <summary>
    /// Moves a node down in the tree to restore heap order.
    /// </summary>
    private void MoveDown(TElement node, int nodeIndex)
    {
        // The node to move down will not actually be swapped every time.
        // Rather, values on the affected path will be moved up, thus leaving a free spot
        // for this value to drop in. Similar optimization as in the insertion sort.

        Debug.Assert(0 <= nodeIndex && nodeIndex < _size);

        var nodes = _nodes;
        var size = _size;

        int i;
        while ((i = GetFirstChildIndex(nodeIndex)) < size)
        {
            // Find the child node with the maximal priority
            var maxChild = nodes[i]!;
            var maxChildIndex = i;

            var childIndexUpperBound = Math.Min(i + Arity, size);
            while (++i < childIndexUpperBound)
            {
                var nextChild = nodes[i]!;
                if (nextChild.CompareTo(maxChild) > 0)
                {
                    maxChild = nextChild;
                    maxChildIndex = i;
                }
            }

            // Heap property is satisfied; insert node in this location.
            if (node.CompareTo(maxChild) >= 0)
            {
                break;
            }

            // Move the maximal child up by one node and
            // continue recursively from its location.
            nodes[nodeIndex] = maxChild;
            maxChild.HeapIndex = nodeIndex;
            nodeIndex = maxChildIndex;
        }

        nodes[nodeIndex] = node;
        node.HeapIndex = nodeIndex;
    }

    /// <summary>
    ///  Enumerates the element and priority pairs of a <see cref="MaxHeap{TElement}"/>
    ///  without any ordering guarantees.
    /// </summary>
    public struct UnorderedElementEnumerable : IEnumerator<TElement>, IEnumerable<TElement>
    {
        private readonly MaxHeap<TElement> _heap;
        private int _index;
        private TElement? _current;

        internal UnorderedElementEnumerable(MaxHeap<TElement> heap)
        {
            _heap = heap;
            _index = 0;
            _current = default;
        }

        /// <summary>
        /// Releases all resources used by the <see cref="UnorderedElementEnumerable"/>.
        /// </summary>
        public readonly void Dispose() { }

        /// <summary>
        /// Advances the enumerator to the next element of the heap.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            var localHeap = _heap;

            if ((uint)_index < (uint)localHeap._size)
            {
                _current = localHeap._nodes[_index];
                _index++;
                return true;
            }

            return MoveNextRare();
        }

        private bool MoveNextRare()
        {
            _index = _heap._size + 1;
            _current = default;
            return false;
        }

        /// <summary>
        /// Gets the element at the current position of the enumerator.
        /// </summary>
        public readonly TElement Current => _current ?? throw new InvalidOperationException("Current element is not valid.");

        readonly object IEnumerator.Current => Current;

        public readonly UnorderedElementEnumerable GetEnumerator() => this;
        readonly IEnumerator<TElement> IEnumerable<TElement>.GetEnumerator() => this;
        void IEnumerator.Reset()
        {
            _index = 0;
            _current = default;
        }

        readonly IEnumerator IEnumerable.GetEnumerator() => this;
    }
}
