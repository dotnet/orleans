namespace Orleans.Dashboard.Metrics.History;

internal sealed class RingBuffer<T>(int capacity)
{
    private readonly T[] _items = new T[capacity];
    private int _startIndex;

    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            var finalIndex = (_startIndex + index) % _items.Length;

            return _items[finalIndex];
        }
    }

    public void Add(T item)
    {
        var newIndex = (_startIndex + Count) % _items.Length;

        _items[newIndex] = item;

        if (Count < _items.Length)
        {
            Count++;
        }
        else
        {
            _startIndex++;
        }
    }
}
