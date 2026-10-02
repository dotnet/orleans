namespace Orleans.Serialization.NativeAotFieldAccessSmoke;

[GenerateSerializer]
internal sealed class PrivateFields
{
    [Id(0)] private int _number;
    [Id(1)] private readonly byte[] _bytes = [];
    [Id(2)] public string ReadOnly { get; } = "";
    [Id(3)] public string InitOnly { get; init; } = "";
    [Id(4)] public string PrivateSetter { get; private set; } = "";
    [Id(5)] private string PrivateProperty { get; set; } = "";

    public PrivateFields()
    {
    }

    public PrivateFields(int number, byte[] bytes)
    {
        _number = number;
        _bytes = bytes;
        ReadOnly = "readonly";
        InitOnly = "init-only";
        PrivateSetter = "private setter";
        PrivateProperty = "private property";
    }

    public int Number => _number;
    public byte[] Bytes => _bytes;
    public string PrivateValue => PrivateProperty;
}

[GenerateSerializer]
internal sealed class ConstrainedFields<T> where T : class, IComparable<T>, IEquatable<T>
{
    [Id(0)] private readonly T _value = default!;
    [Id(1)] public T ReadOnly { get; } = default!;
    [Id(2)] public T InitOnly { get; init; } = default!;

    public ConstrainedFields()
    {
    }

    public ConstrainedFields(T value)
    {
        _value = value;
        ReadOnly = value;
        InitOnly = value;
    }

    public T Value => _value;
}

[GenerateSerializer]
internal struct ValueFields<T> where T : unmanaged
{
    [Id(0)] private readonly T _value;
    [Id(1)] private byte[] _bytes;
    [Id(2)] public T ReadOnly { get; }

    public ValueFields(T value, byte[] bytes)
    {
        _value = value;
        _bytes = bytes;
        ReadOnly = value;
    }

    public T Value => _value;
    public byte[] Bytes => _bytes;
}

internal static class Outer<T> where T : class
{
    [GenerateSerializer]
    internal sealed class Nested<U> where U : struct, IEquatable<U>
    {
        [Id(0)] private readonly T _value = default!;
        [Id(1)] private readonly U _number;

        public Nested()
        {
        }

        public Nested(T value, U number)
        {
            _value = value;
            _number = number;
        }

        public T Value => _value;
        public U Number => _number;
    }
}

[GenerateSerializer]
internal sealed class VolatileFields
{
    [Id(0)] private volatile int _number;
    [Id(1)] private volatile string _text = "";
    [Id(2)] private volatile byte[] _bytes = [];
    [Id(3)] private int _ordinary;

    public VolatileFields()
    {
    }

    public VolatileFields(int number, string text, byte[] bytes, int ordinary)
    {
        _number = number;
        _text = text;
        _bytes = bytes;
        _ordinary = ordinary;
    }

    public int Number => _number;
    public string Text => _text;
    public byte[] Bytes => _bytes;
    public int Ordinary => _ordinary;
}

[GenerateSerializer]
internal struct VolatileValueFields
{
    [Id(0)] private volatile int _number;
    [Id(1)] private volatile byte[] _bytes;

    public VolatileValueFields(int number, byte[] bytes)
    {
        _number = number;
        _bytes = bytes;
    }

    public int Number => _number;
    public byte[] Bytes => _bytes;
}

[GenerateSerializer]
internal sealed class GenericVolatileFields<T> where T : class
{
    [Id(0)] private volatile T _value = default!;

    public GenericVolatileFields()
    {
    }

    public GenericVolatileFields(T value) => _value = value;

    public T Value => _value;
}
