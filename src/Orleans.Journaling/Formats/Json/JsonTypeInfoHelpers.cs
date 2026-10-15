using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

internal static class JsonTypeInfoHelpers
{
    private static readonly JsonSerializerOptions MetadataOnlyOptions = new(JsonSerializerDefaults.General);

    public static JsonTypeInfo<T> GetTypeInfo<T>(JsonSerializerOptions? options)
    {
        options ??= GetDefaultOptions();
        try
        {
            var typeInfo = options.GetTypeInfo(typeof(T));
            if (typeInfo is JsonTypeInfo<T> typedTypeInfo)
            {
                return typedTypeInfo;
            }
        }
        catch (NotSupportedException exception)
        {
            throw CreateMissingMetadataException<T>(exception);
        }
        catch (InvalidOperationException exception) when (options.TypeInfoResolver is null)
        {
            throw CreateMissingMetadataException<T>(exception);
        }

        throw CreateMissingMetadataException<T>();
    }

    // Trimmed and Native AOT publishing sets the System.Text.Json reflection feature switch to false, which removes the
    // JsonSerializerOptions.Default branch. Options without a type-info resolver then report the missing metadata.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "JsonSerializerOptions.Default is used only when System.Text.Json reflection is enabled.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JsonSerializerOptions.Default is used only when System.Text.Json reflection is enabled.")]
    private static JsonSerializerOptions GetDefaultOptions()
        => JsonSerializer.IsReflectionEnabledByDefault ? JsonSerializerOptions.Default : MetadataOnlyOptions;

    private static InvalidOperationException CreateMissingMetadataException<T>(Exception? innerException = null)
    {
        var valueType = typeof(T);
        return new(
            $"JSON journaling requires System.Text.Json metadata for journaled payload type '{valueType.FullName}'. "
            + "Add the type to a source-generated JsonSerializerContext using [JsonSerializable(typeof(...))] and register the context "
            + $"with UseJsonJournalFormat(MyJournalJsonContext.Default) or Configure<{nameof(JsonJournalOptions)}>(options => "
            + $"options.{nameof(JsonJournalOptions.AddTypeInfoResolver)}(MyJournalJsonContext.Default)).",
            innerException);
    }
}
