using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

internal ref struct JsonCommandReader
{
    private Utf8JsonReader _reader;
    private int _nextIndex;

    public JsonCommandReader(ReadOnlySequence<byte> input)
    {
        _reader = new Utf8JsonReader(input, isFinalBlock: true, state: default);
        _nextIndex = 0;
        if (!_reader.Read() || _reader.TokenType is not JsonTokenType.StartArray)
        {
            throw new JsonException("A JSON journal command must be an array.");
        }

        Command = ReadCommandCore();
    }

    public JsonCommandReader(JournalBufferReader input)
    {
        _reader = new Utf8JsonReader(input.AsReadOnlySequence(), isFinalBlock: true, state: default);
        _nextIndex = 0;
        if (!_reader.Read() || _reader.TokenType is not JsonTokenType.StartArray)
        {
            throw new JsonException("A JSON journal command must be an array.");
        }

        Command = ReadCommandCore();
    }

    public JsonCommandReader(ref Utf8JsonReader reader)
    {
        _reader = reader;
        _nextIndex = 0;
        Command = ReadCommandCore();
    }

    public string? Command { get; }

    public T? Deserialize<T>(int index, string operandName, JsonTypeInfo<T> typeInfo)
    {
        ReadOperand(index, operandName);
        return JsonSerializer.Deserialize(ref _reader, typeInfo);
    }

    public T DeserializeRequired<T>(int index, string operandName, JsonTypeInfo<T> typeInfo)
    {
        var value = Deserialize(index, operandName, typeInfo);
        if (value is null)
        {
            throw new JsonException($"JSON journal command operand '{operandName}' must not be null.");
        }

        return value;
    }

    public T? DeserializeCurrent<T>(JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(ref _reader, typeInfo);

    public T DeserializeCurrentRequired<T>(string operandName, JsonTypeInfo<T> typeInfo)
    {
        var value = DeserializeCurrent(typeInfo);
        if (value is null)
        {
            throw new JsonException($"JSON journal command operand '{operandName}' must not be null.");
        }

        return value;
    }

    public int ReadInt32(int index, string operandName)
    {
        ReadOperand(index, operandName);
        if (!_reader.TryGetInt32(out var value))
        {
            throw new JsonException($"JSON journal command operand '{operandName}' must be a 32-bit integer.");
        }

        return value;
    }

    public ulong ReadUInt64(int index, string operandName)
    {
        ReadOperand(index, operandName);
        if (!_reader.TryGetUInt64(out var value))
        {
            throw new JsonException($"JSON journal command operand '{operandName}' must be an unsigned integer.");
        }

        return value;
    }

    public string? ReadString(int index, string operandName)
    {
        ReadOperand(index, operandName);
        if (_reader.TokenType is JsonTokenType.Null)
        {
            return null;
        }

        if (_reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException($"JSON journal command operand '{operandName}' must be a string.");
        }

        return _reader.GetString();
    }

    public int StartArray(int index, string operandName)
    {
        ReadOperand(index, operandName);
        if (_reader.TokenType is not JsonTokenType.StartArray)
        {
            throw new JsonException($"JSON journal command operand '{operandName}' must be an array.");
        }

        return CountCurrentArray(operandName);
    }

    public bool ReadArrayItem(string operandName)
    {
        if (!_reader.Read())
        {
            throw new JsonException($"JSON journal command operand '{operandName}' array is incomplete.");
        }

        return _reader.TokenType is not JsonTokenType.EndArray;
    }

    public (TFirst? First, TSecond? Second) ReadCurrentPair<TFirst, TSecond>(
        string operandName,
        JsonTypeInfo<TFirst> firstTypeInfo,
        JsonTypeInfo<TSecond> secondTypeInfo)
    {
        if (_reader.TokenType is not JsonTokenType.StartArray)
        {
            throw new JsonException("JSON dictionary snapshot items must be [key,value] arrays.");
        }

        if (!_reader.Read() || _reader.TokenType is JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal command is missing operand '{JsonJournalEntryFields.Key}'.");
        }

        var first = JsonSerializer.Deserialize(ref _reader, firstTypeInfo);
        if (!_reader.Read() || _reader.TokenType is JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal command is missing operand '{JsonJournalEntryFields.Value}'.");
        }

        var second = JsonSerializer.Deserialize(ref _reader, secondTypeInfo);
        if (!_reader.Read())
        {
            throw new JsonException($"JSON journal command operand '{operandName}' array is incomplete.");
        }

        if (_reader.TokenType is not JsonTokenType.EndArray)
        {
            throw new JsonException("JSON journal command contains unexpected extra elements.");
        }

        return (first, second);
    }

    public (TFirst First, TSecond Second) ReadCurrentPairRequired<TFirst, TSecond>(
        string operandName,
        JsonTypeInfo<TFirst> firstTypeInfo,
        JsonTypeInfo<TSecond> secondTypeInfo)
    {
        var (first, second) = ReadCurrentPair(operandName, firstTypeInfo, secondTypeInfo);
        if (first is null)
        {
            throw new JsonException($"JSON journal command operand '{JsonJournalEntryFields.Key}' must not be null.");
        }

        if (second is null)
        {
            throw new JsonException($"JSON journal command operand '{JsonJournalEntryFields.Value}' must not be null.");
        }

        return (first, second);
    }

    public void EnsureEnd(int nextIndex)
    {
        if (nextIndex != _nextIndex)
        {
            throw new InvalidOperationException("JSON journal command operands must be read in order.");
        }

        if (!_reader.Read())
        {
            throw new JsonException("JSON journal command array is incomplete.");
        }

        if (_reader.TokenType is not JsonTokenType.EndArray)
        {
            throw new JsonException("JSON journal command contains unexpected extra elements.");
        }

        if (_reader.Read())
        {
            throw new JsonException("Additional JSON content was found after the journal entry.");
        }
    }

    public void SkipToEnd()
    {
        while (_reader.Read())
        {
            if (_reader.TokenType is JsonTokenType.EndArray)
            {
                if (_reader.Read())
                {
                    throw new JsonException("Additional JSON content was found after the journal entry.");
                }

                return;
            }

            _reader.Skip();
        }

        throw new JsonException("JSON journal command array is incomplete.");
    }

    private string? ReadCommandCore()
    {
        if (!_reader.Read())
        {
            throw new JsonException("A JSON journal command array is incomplete.");
        }

        if (_reader.TokenType is JsonTokenType.EndArray)
        {
            throw new JsonException("A JSON journal command array must include a command string.");
        }

        if (_reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException("The first JSON journal command element must be a command string.");
        }

        _nextIndex = 1;
        return _reader.GetString();
    }

    private void ReadOperand(int index, string operandName)
    {
        if (index != _nextIndex)
        {
            throw new InvalidOperationException("JSON journal command operands must be read in order.");
        }

        if (!_reader.Read() || _reader.TokenType is JsonTokenType.EndArray)
        {
            throw new JsonException($"JSON journal command is missing operand '{operandName}'.");
        }

        _nextIndex++;
    }

    private int CountCurrentArray(string operandName)
    {
        var reader = _reader;
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.EndArray)
            {
                return count;
            }

            count++;
            reader.Skip();
        }

        throw new JsonException($"JSON journal command operand '{operandName}' array is incomplete.");
    }
}
