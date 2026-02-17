using Orleans.Runtime.Placement.Repartitioning;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

/// <summary>
/// Tests for the max heap data structure used in activation repartitioning algorithms.
/// </summary>
public sealed class MaxHeapTests
{
    public class MyHeapElement(int value) : IHeapElement<MyHeapElement>
    {
        public int Value { get; set; } = value;

        public int HeapIndex { get; set; } = -1;

        public int CompareTo(MyHeapElement other) => Value.CompareTo(other.Value);
        public override string ToString() => $"{Value} @ {HeapIndex}";
    }

    [Fact]
    public void HeapPropertyIsMaintained()
    {
        var edges = new MyHeapElement[100];
        for (int i = 0; i < edges.Length; i++)
        {
            edges[i] = new MyHeapElement(i);
        }

        Random.Shared.Shuffle(edges);
        var heap = new MaxHeap<MyHeapElement>([.. edges]);
        Assert.Equal(100, heap.Count);
        Assert.Equal(99, heap.Peek().Value);
        Assert.Equal(99, heap.Peek().Value);
        Assert.Equal(99, heap.Pop().Value);
        Assert.Equal(98, heap.Pop().Value);
        Assert.Equal(98, heap.Count);
        Assert.Equal(98, heap.UnorderedElements.Count());

        var unorderedElements = heap.UnorderedElements.ToArray();
        var edge = unorderedElements[Random.Shared.Next(unorderedElements.Length)];
        edge.Value = 2000;
        heap.OnIncreaseElementPriority(edge);
        Assert.Equal(2000, heap.Peek().Value);

        // Randomly re-assign priorities to edges
        var newScore = 100;
        var elements = heap.UnorderedElements.ToArray();
        Random.Shared.Shuffle(elements);
        foreach (var element in elements)
        {
            var originalValue = element.Value;
            element.Value = newScore--;
            if (element.Value > originalValue)
            {
                heap.OnIncreaseElementPriority(element);
            }
            else
            {
                heap.OnDecreaseElementPriority(element);
            }
        }

        Assert.Equal(98, heap.UnorderedElements.Count());
        var allElements = new List<MyHeapElement>();
        while (heap.Count > 0)
        {
            allElements.Add(heap.Pop());
        }

        Assert.Equal(98, allElements.Count);

        var copy = allElements.ToList();
        copy.Sort((a, b) => b.Value.CompareTo(a.Value));
        var expected = string.Join(", ", Enumerable.Range(0, 98).Select(i => 100 - i));
        var actual = string.Join(", ", allElements.Select(c => c.Value));
        Assert.Equal(expected, actual);
    }
}
