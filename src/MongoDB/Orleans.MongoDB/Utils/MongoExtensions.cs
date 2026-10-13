using System;
using System.Linq.Expressions;
using MongoDB.Driver;
using Orleans.Providers.MongoDB.Configuration;

namespace Orleans.Providers.MongoDB.Utils;

internal static class MongoExtensions
{
    public static bool IsDuplicateKey(this MongoException ex)
    {
        if (ex is MongoCommandException c && c.Code == 11000)
        {
            return true;
        }

        if (ex is MongoWriteException w
            && w.WriteError.Category == ServerErrorCategory.DuplicateKey
            && w.WriteError.Message.Contains("index: _id_ ", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool IsDuplicateIndex(this MongoCommandException ex) => ex.Code == 85;

    internal static string GetFieldName<T>(this IMongoCollection<T> collection, Expression<Func<T, object>> expression)
    {
        return new ExpressionFieldDefinition<T>(expression)
            .Render(new RenderArgs<T>(collection.DocumentSerializer, collection.Settings.SerializerRegistry))
            .FieldName;
    }

    public static IMongoClient Create(this IMongoClientFactory mongoClientFactory, MongoDBOptions options, string defaultName)
    {
        var name = options.ClientName;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = defaultName;
        }

        return mongoClientFactory.Create(name);
    }
}
