using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Orleans.MongoDB.Tests.Persistence;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.StorageProviders;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Serialization;
using Xunit;

namespace Orleans.MongoDB.Tests.Serializers;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Serialization")]
public sealed class MongoSerializerTests
{
    [Fact]
    public void JsonSerializer_CustomSettingsProduceConcreteBsonFields()
    {
        using var host = MongoTestHost.Create();
        var serializer = new JsonGrainStateSerializer(Options.Create(new JsonGrainStateSerializerOptions
        {
            ConfigureJsonSerializerSettings = settings =>
            {
                settings.TypeNameHandling = TypeNameHandling.None;
                settings.ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() };
            }
        }), host.Services);
        var document = serializer.Serialize(new MongoTestState
        {
            Name = "Ada",
            Count = 42,
            Address = new MongoTestAddress { City = "Zürich" }
        }).AsBsonDocument;
        Assert.Equal("Ada", document["name"].AsString);
        Assert.Equal(42, document["count"].ToInt32());
        Assert.Equal("Zürich", document["address"]["city"].AsString);
        Assert.False(document.Contains("Name"));
        var fixedDocument = new BsonDocument { { "name", "Grace" }, { "count", 7 }, { "address", new BsonDocument("city", "Paris") } };
        var restored = serializer.Deserialize<MongoTestState>(fixedDocument);
        Assert.Equal("Grace", restored.Name);
        Assert.Equal(7, restored.Count);
        Assert.Equal("Paris", restored.Address.City);
    }

    [Fact]
    public void JsonSerializer_DefaultSettingsOverwriteNonSystemConstructorDefaults()
    {
        using var host = MongoTestHost.Create();
        var serializer = new JsonGrainStateSerializer(Options.Create(new JsonGrainStateSerializerOptions()), host.Services);
        var document = serializer.Serialize(new ConstructorDefaults { Number = 0, Text = null }).AsBsonDocument;
        Assert.True(document["Text"].IsBsonNull);
        // DefaultValueHandling.Populate omits zero but must reset a non-zero constructor value when reading.
        var restored = serializer.Deserialize<ConstructorDefaults>(new BsonDocument("Text", BsonNull.Value));
        Assert.Equal(0, restored.Number);
        Assert.Null(restored.Text);
    }

    [Fact]
    public void BsonSerializer_SerializesInspectableDocumentAndDeserializesFixedDocument()
    {
        var serializer = new BsonGrainStateSerializer();
        var document = serializer.Serialize(new MongoTestState { Name = "Ada", Count = 42 }).AsBsonDocument;
        // Materialize the BsonDocumentWrapper just as MongoDB.Driver does during writes.
        document = BsonSerializer.Deserialize<BsonDocument>(document.ToBson());
        Assert.Equal("Ada", document["Name"].AsString);
        Assert.Equal(42, document["Count"].ToInt32());
        Assert.False(document.Contains("data"));
        var restored = serializer.Deserialize<MongoTestState>(new BsonDocument
        {
            { "Name", "Grace" }, { "Count", 7 }, { "Address", new BsonDocument("City", "Paris") }
        });
        Assert.Equal("Grace", restored.Name);
        Assert.Equal(7, restored.Count);
        Assert.Equal("Paris", restored.Address.City);
    }

    [Theory, InlineData(0), InlineData(42), InlineData(-7)]
    public void BsonSerializer_DeserializesPrimitiveValueWrapper(int value)
    {
        var serializer = new BsonGrainStateSerializer();
        Assert.Equal(value, serializer.Deserialize<int>(new BsonInt32(value)));
    }

    [Fact]
    public void BinarySerializer_UsesOrleansPayloadInDataField()
    {
        using var host = MongoTestHost.Create();
        var orleansSerializer = host.Services.GetRequiredService<Serializer>();
        var serializer = new BinaryGrainStateSerializer(orleansSerializer);
        var document = serializer.Serialize(new MongoTestState
        {
            Name = "Ada",
            Count = 42,
            Address = new MongoTestAddress { City = "Zürich" }
        }).AsBsonDocument;
        Assert.Equal(new[] { "data" }, document.Names);
        var independentlyDecoded = orleansSerializer.Deserialize<MongoTestState>(document["data"].AsByteArray);
        Assert.NotNull(independentlyDecoded);
        Assert.Equal("Ada", independentlyDecoded.Name);
        Assert.Equal(42, independentlyDecoded.Count);
        Assert.Equal("Zürich", independentlyDecoded.Address.City);
        var fixedPayload = orleansSerializer.SerializeToArray(new MongoTestState { Name = "Grace", Count = 7 });
        var restored = serializer.Deserialize<MongoTestState>(new BsonDocument("data", new BsonBinaryData(fixedPayload)));
        Assert.Equal("Grace", restored.Name);
        Assert.Equal(7, restored.Count);
        Assert.Equal("", restored.Address.City);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("$", "__")]
    [InlineData("$type", "__type")]
    [InlineData("a$b", "a$b")]
    [InlineData("_", "_")]
    public void JsonBsonConverter_EscapesOnlyLeadingDollarKeys(string jsonKey, string bsonKey)
    {
        var converted = new JObject { [jsonKey] = 42, ["neighbor"] = "retained" }.ToBson();
        Assert.Equal(42, converted[bsonKey].ToInt32());
        Assert.Equal("retained", converted["neighbor"].AsString);
        Assert.Equal(2, converted.ElementCount);
        var decoded = new BsonDocument { { bsonKey, 7 }, { "neighbor", "fixed" } }.ToJToken();
        Assert.Equal(7, decoded[jsonKey]!.Value<int>());
        Assert.Equal("fixed", decoded["neighbor"]!.Value<string>());
    }

    [Fact]
    public void JsonBsonConverter_ConvertsNestedArraysNullAndNumericTypes()
    {
        var input = new JObject
        {
            ["$type"] = "Account",
            ["nested"] = new JObject { ["$value"] = new JArray(42, true, JValue.CreateNull(), "Привет") },
            ["large"] = new JValue(9007199254740993L),
            ["decimal"] = new JValue(12.25m),
            ["double"] = new JValue(-3.5d),
            ["false"] = false
        };
        var document = input.ToBson();
        Assert.Equal("Account", document["__type"].AsString);
        Assert.Equal(9007199254740993L, document["large"].AsInt64);
        Assert.Equal(12.25m, document["decimal"].AsDecimal);
        Assert.Equal(-3.5d, document["double"].AsDouble);
        Assert.False(document["false"].AsBoolean);
        var array = document["nested"]["__value"].AsBsonArray;
        Assert.Equal(42, array[0].ToInt32());
        Assert.True(array[1].AsBoolean);
        Assert.True(array[2].IsBsonNull);
        Assert.Equal("Привет", array[3].AsString);
        var decoded = new BsonDocument("items", new BsonArray { new BsonInt64(9007199254740993L), new BsonDecimal128(12.25m), BsonNull.Value }).ToJToken();
        Assert.Equal(9007199254740993L, decoded["items"]![0]!.Value<long>());
        Assert.Equal(12.25m, decoded["items"]![1]!.Value<decimal>());
        Assert.Equal(JTokenType.Null, decoded["items"]![2]!.Type);
        Assert.Equal(-3.5d, new BsonDouble(-3.5d).ToJToken().Value<double>());
        Assert.False(new BsonBoolean(false).ToJToken().Value<bool>());
    }

    [Fact]
    public void JsonBsonConverter_PreservesDateTicksAndUtcOffsetNormalization()
    {
        var date = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567);
        Assert.Equal("2026-01-02T03:04:05.1234567Z", JsonBsonConverter.ToBson((JToken)new JValue(date)).AsString);
        var offset = new DateTimeOffset(2026, 1, 2, 4, 4, 5, TimeSpan.FromHours(1)).AddTicks(1234567);
        Assert.Equal("2026-01-02T03:04:05.1234567Z", JsonBsonConverter.ToBson((JToken)new JValue(offset)).AsString);
        Assert.Equal(date.AddTicks(-1234567), new BsonDateTime(date.AddTicks(-1234567)).ToJToken().Value<DateTime>());
    }

    [Fact]
    public void JsonBsonConverter_ConvertsBinaryAndStringEncodedScalarTypes()
    {
        var guid = new Guid("aabbccdd-1234-5678-abcd-0123456789ab");
        var bytes = new byte[] { 0, 127, 255 };
        var document = new JObject
        {
            ["bytes"] = new JValue(bytes),
            ["guid"] = new JValue(guid),
            ["uri"] = new JValue(new Uri("https://example.invalid/a%20b")),
            ["duration"] = new JValue(TimeSpan.FromSeconds(90)),
            ["undefined"] = JValue.CreateUndefined()
        }.ToBson();
        Assert.Equal(bytes, document["bytes"].AsBsonBinaryData.Bytes);
        Assert.Equal("aabbccdd-1234-5678-abcd-0123456789ab", document["guid"].AsString);
        Assert.Equal("https://example.invalid/a b", document["uri"].AsString);
        Assert.Equal("00:01:30", document["duration"].AsString);
        Assert.Equal(BsonType.Undefined, document["undefined"].BsonType);
        Assert.Equal(new byte[] { 1, 2 }, new BsonBinaryData(new byte[] { 1, 2 }).ToJToken().Value<byte[]>());
        Assert.Equal(JTokenType.Undefined, BsonUndefined.Value.ToJToken().Type);
    }

    [Fact]
    public void JsonBsonConverter_UnsupportedTypesThrowRatherThanLoseData()
    {
        var bson = Assert.Throws<NotSupportedException>(() => new BsonObjectId(ObjectId.Empty).ToJToken());
        Assert.Contains("Json", bson.Message);
        var json = Assert.Throws<NotSupportedException>(() => JsonBsonConverter.ToBson((JToken)new JConstructor("unsupported", 42)));
        Assert.Contains("Bson", json.Message);
    }

    public sealed class ConstructorDefaults
    {
        public int Number { get; set; } = 99;
        public string? Text { get; set; } = "constructor-default";
    }
}
