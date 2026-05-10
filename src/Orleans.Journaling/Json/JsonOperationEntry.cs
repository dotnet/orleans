using System.Text.Json;

namespace Orleans.Journaling.Json;

internal readonly struct JsonOperationEntry
{
    private readonly JsonElement _array;
    private readonly int _offset;

    public JsonOperationEntry(JsonElement array)
        : this(array, 0, GetArrayLength(array))
    {
    }

    public JsonOperationEntry(JsonElement array, int offset, int length)
    {
        var arrayLength = GetArrayLength(array);
        if ((uint)offset > (uint)arrayLength)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (length < 0 || length > arrayLength - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _array = array;
        _offset = offset;
        Length = length;
    }

    public int Length { get; }

    public JsonElement this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
            {
                throw new IndexOutOfRangeException();
            }

            return _array[_offset + index];
        }
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        WriteArrayElementsTo(writer);
        writer.WriteEndArray();
    }

    public void WriteArrayElementsTo(Utf8JsonWriter writer)
    {
        for (var i = 0; i < Length; i++)
        {
            this[i].WriteTo(writer);
        }
    }

    private static int GetArrayLength(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException("A JSON journal operation must be an array.");
        }

        return element.GetArrayLength();
    }
}
