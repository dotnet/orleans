using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

internal readonly struct JsonOperationEntry
{
    private readonly JsonElement _array;
    private readonly int _offset;

    public JsonOperationEntry(JsonElement array)
        : this(array, 0, GetArrayLength(array))
    {
    }

    public JsonOperationEntry(JsonElement array, int offset)
        : this(array, offset, GetArrayLength(array) - offset)
    {
    }

    public JsonOperationEntry(JsonElement array, int offset, int length)
    {
        var arrayLength = GetArrayLength(array);
        if ((uint)offset > (uint)arrayLength || length < 0 || offset + length > arrayLength)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
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

    public static JsonOperationEntry Parse(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input, isFinalBlock: true, state: default);
        var result = JsonElement.ParseValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("Additional JSON content was found after the log entry.");
        }

        return new(result);
    }

    public JsonOperationEntry Slice(int start)
    {
        if ((uint)start > (uint)Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        return new(_array, _offset + start, Length - start);
    }

    public string? ReadCommand()
    {
        if (Length == 0)
        {
            throw new JsonException("A JSON journal operation array must include a command string.");
        }

        var command = this[0];
        if (command.ValueKind is not JsonValueKind.String)
        {
            throw new JsonException("The first JSON journal operation element must be a command string.");
        }

        return command.GetString();
    }

    public JsonElement ReadElement(int index, string operandName)
    {
        if ((uint)index >= (uint)Length)
        {
            throw new JsonException($"JSON journal operation is missing operand '{operandName}'.");
        }

        return this[index];
    }

    public T? Deserialize<T>(int index, string operandName, JsonTypeInfo<T> typeInfo)
    {
        return ReadElement(index, operandName).Deserialize(typeInfo);
    }

    public int ReadInt32(int index, string operandName)
    {
        var element = ReadElement(index, operandName);
        if (!element.TryGetInt32(out var value))
        {
            throw new JsonException($"JSON journal operation operand '{operandName}' must be a 32-bit integer.");
        }

        return value;
    }

    public ulong ReadUInt64(int index, string operandName)
    {
        var element = ReadElement(index, operandName);
        if (!element.TryGetUInt64(out var value))
        {
            throw new JsonException($"JSON journal operation operand '{operandName}' must be an unsigned integer.");
        }

        return value;
    }

    public string? ReadString(int index, string operandName)
    {
        var element = ReadElement(index, operandName);
        if (element.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind is not JsonValueKind.String)
        {
            throw new JsonException($"JSON journal operation operand '{operandName}' must be a string.");
        }

        return element.GetString();
    }

    public JsonElement.ArrayEnumerator ReadArray(int index, string operandName)
        => ReadArrayElement(index, operandName).EnumerateArray();

    public JsonElement ReadArrayElement(int index, string operandName)
    {
        var element = ReadElement(index, operandName);
        if (element.ValueKind is not JsonValueKind.Array)
        {
            throw new JsonException($"JSON journal operation operand '{operandName}' must be an array.");
        }

        return element;
    }

    public void EnsureEnd(int nextIndex)
    {
        if (Length > nextIndex)
        {
            throw new JsonException("JSON journal operation contains unexpected extra elements.");
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
