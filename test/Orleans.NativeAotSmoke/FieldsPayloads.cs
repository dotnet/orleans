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
