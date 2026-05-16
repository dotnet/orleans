using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

internal static class JsonTypeInfoHelpers
{
    public static JsonTypeInfo<T> GetTypeInfo<T>(JsonSerializerOptions? options)
    {
        options ??= JsonSerializerOptions.Default;
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

    private static InvalidOperationException CreateMissingMetadataException<T>(Exception? innerException = null)
    {
        var valueType = typeof(T);
        return new(
            $"JSON journaling requires System.Text.Json metadata for journaled payload type '{valueType.FullName}'. "
            + "Add the type to a source-generated JsonSerializerContext using [JsonSerializable(typeof(...))] and register the context "
            + $"with UseJsonJournalFormat(MyJournalJsonContext.Default), {nameof(JsonJournalOptions)}.{nameof(JsonJournalOptions.AddTypeInfoResolver)}, "
            + $"or {nameof(JsonJournalOptions)}.{nameof(JsonJournalOptions.SerializerOptions)}.{nameof(JsonSerializerOptions.TypeInfoResolver)}.",
            innerException);
    }
}
