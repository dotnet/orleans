using System;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;

namespace Orleans.Providers.MongoDB.StorageProviders;

internal static class JsonBsonConverter
{
    public static BsonDocument ToBson(this JObject source)
    {
        var result = new BsonDocument();

        foreach (var property in source)
        {
            result.Add(property.Key.EscapeJson(), property.Value!.ToBson());
        }

        return result;
    }

    public static JObject ToJToken(this BsonDocument source)
    {
        var result = new JObject();

        foreach (var property in source)
        {
            result.Add(property.Name.UnescapeBson(), property.Value.ToJToken());
        }

        return result;
    }

    public static BsonArray ToBson(this JArray source)
    {
        var result = new BsonArray();

        foreach (var item in source)
        {
            result.Add(item.ToBson());
        }

        return result;
    }

    public static JArray ToJToken(this BsonArray source)
    {
        var result = new JArray();

        foreach (var item in source)
        {
            result.Add(item.ToJToken());
        }

        return result;
    }

    public static BsonValue ToBson(this JToken source)
    {
        switch (source.Type)
        {
            case JTokenType.Object:
                return ((JObject)source).ToBson();
            case JTokenType.Array:
                return ((JArray)source).ToBson();
            case JTokenType.Integer:
                return BsonValue.Create(((JValue)source).Value);
            case JTokenType.Float:
                return BsonValue.Create(((JValue)source).Value);
            case JTokenType.String:
                return BsonValue.Create(((JValue)source).Value);
            case JTokenType.Boolean:
                return BsonValue.Create(((JValue)source).Value);
            case JTokenType.Null:
                return BsonNull.Value;
            case JTokenType.Undefined:
                return BsonUndefined.Value;
            case JTokenType.Bytes:
                return BsonValue.Create(((JValue)source).Value);
            case JTokenType.Guid:
                return BsonValue.Create(((JValue)source).ToString());
            case JTokenType.Uri:
                return BsonValue.Create(((JValue)source).ToString());
            case JTokenType.TimeSpan:
                return BsonValue.Create(((JValue)source).ToString());
            case JTokenType.Date:
                {
                    var value = ((JValue)source).Value;

                    if (value is DateTime dateTime)
                    {
                        return dateTime.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK");
                    }
                    else if (value is DateTimeOffset dateTimeOffset)
                    {
                        return dateTimeOffset.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK");
                    }
                    else
                    {
                        return value!.ToString();
                    }
                }
        }

        throw new NotSupportedException($"Cannot convert {source.GetType()} to Bson.");
    }

    public static JToken ToJToken(this BsonValue source)
    {
        switch (source.BsonType)
        {
            case BsonType.Document:
                return source.AsBsonDocument.ToJToken();
            case BsonType.Array:
                return source.AsBsonArray.ToJToken();
            case BsonType.Double:
                return new JValue(source.AsDouble);
            case BsonType.String:
                return new JValue(source.AsString);
            case BsonType.Boolean:
                return new JValue(source.AsBoolean);
            case BsonType.DateTime:
                return new JValue(source.ToUniversalTime());
            case BsonType.Int32:
                return new JValue(source.AsInt32);
            case BsonType.Int64:
                return new JValue(source.AsInt64);
            case BsonType.Decimal128:
                return new JValue(source.AsDecimal);
            case BsonType.Binary:
                return new JValue(source.AsBsonBinaryData.Bytes);
            case BsonType.Null:
                return JValue.CreateNull();
            case BsonType.Undefined:
                return JValue.CreateUndefined();
        }

        throw new NotSupportedException($"Cannot convert {source.GetType()} to Json.");
    }

    private static string EscapeJson(this string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        if (value[0] == '$')
        {
            return "__" + value.Substring(1);
        }

        return value;
    }

    private static string UnescapeBson(this string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        if (value[0] == '_' && value[1] == '_')
        {
            return "$" + value.Substring(2);
        }

        return value;
    }
}
