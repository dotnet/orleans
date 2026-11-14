// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Orleans.Transactions.AdoNet.Utils;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

/// <summary>
/// Unit tests for <see cref="JsonUtils"/> — pure static UTF-8 / Newtonsoft.Json wrappers.
/// No external dependencies, no database.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
public sealed class JsonUtilsTests
{
    // Helper state type used across tests.
    private sealed record SimpleState(string Name, int Count, List<int>? Items);
    private sealed record SettingsState(string MultiWordValue);

    private static JsonSerializerSettings DefaultSettings() => new JsonSerializerSettings();

    // -----------------------------------------------------------------------
    // Roundtrip
    // -----------------------------------------------------------------------

    [Fact]
    public void Roundtrip_SimpleObject()
    {
        var original = new SimpleState("hello", 42, null);
        var settings = DefaultSettings();

        var bytes = JsonUtils.SerializeWithNewtonsoftJson(original, settings);
        var result = JsonUtils.DeserializeWithNewtonsoftJson<SimpleState>(bytes, settings);

        Assert.NotNull(result);
        Assert.Equal(original.Name, result.Name);
        Assert.Equal(original.Count, result.Count);
        Assert.Null(result.Items);
    }

    [Fact]
    public void Roundtrip_NullableProperties()
    {
        var original = new SimpleState(null!, 0, null);
        var settings = DefaultSettings();

        var bytes = JsonUtils.SerializeWithNewtonsoftJson(original, settings);
        var result = JsonUtils.DeserializeWithNewtonsoftJson<SimpleState>(bytes, settings);

        Assert.NotNull(result);
        Assert.Null(result.Name);
        Assert.Null(result.Items);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void Roundtrip_ListProperty()
    {
        var original = new SimpleState("list-test", 3, new List<int> { 1, 2, 3 });
        var settings = DefaultSettings();

        var bytes = JsonUtils.SerializeWithNewtonsoftJson(original, settings);
        var result = JsonUtils.DeserializeWithNewtonsoftJson<SimpleState>(bytes, settings);

        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(3, result.Items!.Count);
        Assert.Equal(new List<int> { 1, 2, 3 }, result.Items);
    }

    // -----------------------------------------------------------------------
    // Serialize
    // -----------------------------------------------------------------------

    [Fact]
    public void Serialize_ProducesUtf8Bytes()
    {
        var obj = new SimpleState("hello", 1, null);
        var settings = DefaultSettings();

        var bytes = JsonUtils.SerializeWithNewtonsoftJson(obj, settings);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("hello", json);
    }

    [Fact]
    public void Serialize_NullObject_ProducesNullLiteral()
    {
        // Serializing a null reference should produce the JSON null literal.
        var settings = DefaultSettings();

        var bytes = JsonUtils.SerializeWithNewtonsoftJson(null!, settings);

        var json = Encoding.UTF8.GetString(bytes);
        Assert.Equal("null", json);
    }

    [Fact]
    public void Serialize_JsonSettingsAreApplied_IndentedFormatting()
    {
        var obj = new SimpleState("fmt", 7, null);
        var settings = new JsonSerializerSettings { Formatting = Formatting.Indented };

        var bytes = JsonUtils.SerializeWithNewtonsoftJson(obj, settings);
        var json = Encoding.UTF8.GetString(bytes);

        // Indented formatting produces newlines — settings were honoured.
        Assert.Contains("\n", json);
    }

    // -----------------------------------------------------------------------
    // Deserialize
    // -----------------------------------------------------------------------

    [Fact]
    public void Deserialize_ValidJson_ReturnsObject()
    {
        var json = """{"Name":"Alice","Count":99,"Items":null}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        var result = JsonUtils.DeserializeWithNewtonsoftJson<SimpleState>(bytes, DefaultSettings());

        Assert.NotNull(result);
        Assert.Equal("Alice", result.Name);
        Assert.Equal(99, result.Count);
    }

    [Fact]
    public void Deserialize_AppliesSerializerSettings()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"multi_word_value":"Configured"}""");
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            }
        };

        var result = JsonUtils.DeserializeWithNewtonsoftJson<SettingsState>(bytes, settings);

        Assert.Equal("Configured", result.MultiWordValue);
    }

    [Fact]
    public void Deserialize_ValidJsonList_ReturnsCorrectList()
    {
        var json = """{"Name":"Bob","Count":2,"Items":[10,20]}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        var result = JsonUtils.DeserializeWithNewtonsoftJson<SimpleState>(bytes, DefaultSettings());

        Assert.NotNull(result?.Items);
        Assert.Equal(2, result!.Items!.Count);
        Assert.Equal(10, result.Items[0]);
        Assert.Equal(20, result.Items[1]);
    }

    [Fact]
    public void Serialize_ThenDeserialize_IntegerArray_PreservesOrder()
    {
        // Verify byte-order of an integer array is preserved end-to-end.
        var original = new List<int> { 100, 200, 300 };
        var settings = DefaultSettings();

        var bytes = JsonUtils.SerializeWithNewtonsoftJson(original, settings);
        var result = JsonUtils.DeserializeWithNewtonsoftJson<List<int>>(bytes, settings);

        Assert.Equal(original, result);
    }
}
