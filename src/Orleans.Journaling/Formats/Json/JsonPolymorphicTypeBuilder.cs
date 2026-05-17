using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

/// <summary>
/// Configures the derived types which JSON journaling can write and read for a polymorphic payload base type.
/// </summary>
/// <typeparam name="TBase">The base type used by the journaled payload.</typeparam>
public sealed class JsonPolymorphicTypeBuilder<TBase> where TBase : class
{
    private readonly JsonPolymorphicTypeRegistry<TBase> _registry;

    internal JsonPolymorphicTypeBuilder(JsonPolymorphicTypeRegistry<TBase> registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Registers a derived payload type with an optional stable discriminator.
    /// </summary>
    /// <typeparam name="TDerived">The derived payload type.</typeparam>
    /// <param name="typeDiscriminator">The discriminator written to the journal. When omitted, <typeparamref name="TDerived"/>'s simple type name is used.</param>
    /// <returns>This builder for chaining.</returns>
    /// <remarks>
    /// Prefer explicit stable discriminators for production journals so type renames do not change the persisted wire format.
    /// </remarks>
    public JsonPolymorphicTypeBuilder<TBase> AddDerivedType<TDerived>(string? typeDiscriminator = null)
        where TDerived : class, TBase
    {
        _registry.Add<TDerived>(typeDiscriminator);
        return this;
    }
}

internal sealed class JsonPolymorphicTypeRegistry<TBase> where TBase : class
{
    private readonly Dictionary<string, Type> _typesByDiscriminator = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _discriminatorsByType = [];
    private bool _isReadOnly;

    public JsonPolymorphicTypeRegistry(string typeDiscriminatorPropertyName)
    {
        TypeDiscriminatorPropertyName = typeDiscriminatorPropertyName;
    }

    public string TypeDiscriminatorPropertyName { get; }

    public void Add<TDerived>(string? typeDiscriminator)
        where TDerived : class, TBase
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException($"Polymorphic JSON journaling for payload base type '{typeof(TBase).FullName}' is already in use and cannot be modified.");
        }

        typeDiscriminator ??= typeof(TDerived).Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(typeDiscriminator);

        if (_discriminatorsByType.ContainsKey(typeof(TDerived)))
        {
            throw new ArgumentException($"Derived payload type '{typeof(TDerived).FullName}' is already registered for payload base type '{typeof(TBase).FullName}'.", nameof(typeDiscriminator));
        }

        if (_typesByDiscriminator.ContainsKey(typeDiscriminator))
        {
            throw new ArgumentException($"Type discriminator '{typeDiscriminator}' is already registered for payload base type '{typeof(TBase).FullName}'.", nameof(typeDiscriminator));
        }

        _typesByDiscriminator.Add(typeDiscriminator, typeof(TDerived));
        _discriminatorsByType.Add(typeof(TDerived), typeDiscriminator);
    }

    public bool TryGetType(string typeDiscriminator, out Type type)
    {
        EnsureReadOnly();
        return _typesByDiscriminator.TryGetValue(typeDiscriminator, out type!);
    }

    public bool TryGetDiscriminator(Type type, out string typeDiscriminator)
    {
        EnsureReadOnly();
        return _discriminatorsByType.TryGetValue(type, out typeDiscriminator!);
    }

    private void EnsureReadOnly()
    {
        if (_typesByDiscriminator.Count == 0)
        {
            throw new InvalidOperationException($"Polymorphic JSON journaling for payload base type '{typeof(TBase).FullName}' must register at least one derived payload type.");
        }

        _isReadOnly = true;
    }
}

internal sealed class JsonPolymorphicTypeConverter<TBase>(JsonPolymorphicTypeRegistry<TBase> registry) : JsonConverter<TBase>
    where TBase : class
{
    private const string ValuePropertyName = "$value";

    public override TBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException($"A polymorphic JSON journal payload for base type '{typeof(TBase).FullName}' must be an object.");
        }

        string? typeDiscriminator = null;
        JsonElement? value = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals(registry.TypeDiscriminatorPropertyName))
            {
                if (property.Value.ValueKind is not JsonValueKind.String)
                {
                    throw new JsonException($"Polymorphic JSON journal payload discriminator '{registry.TypeDiscriminatorPropertyName}' must be a string.");
                }

                typeDiscriminator = property.Value.GetString();
            }
            else if (property.NameEquals(ValuePropertyName))
            {
                value = property.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(typeDiscriminator))
        {
            throw new JsonException($"Polymorphic JSON journal payload is missing discriminator property '{registry.TypeDiscriminatorPropertyName}'.");
        }

        if (value is not { } payload)
        {
            throw new JsonException($"Polymorphic JSON journal payload is missing value property '{ValuePropertyName}'.");
        }

        if (!registry.TryGetType(typeDiscriminator, out var type))
        {
            throw new JsonException($"Polymorphic JSON journal payload discriminator '{typeDiscriminator}' is not registered for base type '{typeof(TBase).FullName}'.");
        }

        var typeInfo = GetTypeInfo(type, options);
        var payloadBytes = Encoding.UTF8.GetBytes(payload.GetRawText());
        var payloadReader = new Utf8JsonReader(payloadBytes);
        return (TBase?)JsonSerializer.Deserialize(ref payloadReader, typeInfo);
    }

    public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var type = value.GetType();
        if (!registry.TryGetDiscriminator(type, out var typeDiscriminator))
        {
            throw new JsonException($"Runtime payload type '{type.FullName}' is not registered for polymorphic JSON journaling base type '{typeof(TBase).FullName}'.");
        }

        writer.WriteStartObject();
        writer.WriteString(registry.TypeDiscriminatorPropertyName, typeDiscriminator);
        writer.WritePropertyName(ValuePropertyName);
        JsonSerializer.Serialize(writer, value, GetTypeInfo(type, options));
        writer.WriteEndObject();
    }

    private static JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        try
        {
            return options.GetTypeInfo(type);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"JSON journaling requires System.Text.Json metadata for polymorphic payload type '{type.FullName}'. " +
                $"Add the type to a source-generated JsonSerializerContext and register it with {nameof(JsonJournalOptions)}.{nameof(JsonJournalOptions.AddTypeInfoResolver)}, " +
                $"or configure {nameof(JsonJournalOptions)}.{nameof(JsonJournalOptions.SerializerOptions)}.{nameof(JsonSerializerOptions.TypeInfoResolver)}.",
                exception);
        }
    }
}
