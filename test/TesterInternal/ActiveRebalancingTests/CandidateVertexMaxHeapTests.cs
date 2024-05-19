using Orleans.Placement.Rebalancing;
using Orleans.Runtime.Placement.Rebalancing;
using Xunit;

namespace UnitTests.ActiveRebalancingTests;

public sealed class CandidateVertexMaxHeapTests
{
    [Fact]
    void HeapPropertyIsMaintained()
    {
        var edges = new CandidateVertex[100];
        for (int i = 0; i < edges.Length; i++)
        {
            edges[i] = new CandidateVertex { AccumulatedTransferScore = i };
        }

        Random.Shared.Shuffle(edges);
        var heap = new CandidateVertexMaxHeap(edges);
        Assert.Equal(100, heap.Count);
        Assert.Equal(99, heap.Peek().AccumulatedTransferScore);
        Assert.Equal(99, heap.Peek().AccumulatedTransferScore);
        Assert.Equal(99, heap.Pop().AccumulatedTransferScore);
        Assert.Equal(98, heap.Pop().AccumulatedTransferScore);
        Assert.Equal(98, heap.Count);
        Assert.Equal(98, heap.UnorderedElements.Count());

        // Randomly re-assign priorities to edges
        var newScore = 1000;
        var elements = heap.UnorderedElements.ToArray();
        Random.Shared.Shuffle(edges);

        foreach (var element in elements)
        {
            element.AccumulatedTransferScore = newScore--;
        }

        heap.Heapify();

        Assert.Equal(1000, heap.Peek().AccumulatedTransferScore);
        Assert.Equal(1000, heap.Peek().AccumulatedTransferScore);
        Assert.Equal(1000, heap.Pop().AccumulatedTransferScore);
        Assert.Equal(999, heap.Pop().AccumulatedTransferScore);
        Assert.Equal(96, heap.Count);
        Assert.Equal(96, heap.UnorderedElements.Count());
    }
}
