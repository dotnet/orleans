using System;
using System.Linq.Expressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Orleans.Runtime;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.MongoDB;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.MongoDB;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.MongoDB;
#else
#error Define a MongoDB provider namespace.
#endif

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

    public static IMongoClient GetMongoClient(this IServiceProvider services, MongoDBOptions options)
    {
        var name = options.ClientName;

        return string.IsNullOrWhiteSpace(name)
            ? services.GetRequiredService<IMongoClient>()
            : services.GetRequiredKeyedService<IMongoClient>(name);
    }

    public static void ConfigureMongoDBOptions(this MongoDBOptions options, IServiceProvider services, IConfiguration configuration)
    {
        configuration.Bind(options);
        var serviceKey = configuration["ServiceKey"];
        if (!string.IsNullOrWhiteSpace(serviceKey))
        {
            if (!string.IsNullOrWhiteSpace(options.ClientName) && options.ClientName != serviceKey)
            {
                throw new OrleansConfigurationException("MongoDB ClientName and ServiceKey must select the same registered client.");
            }

            options.ClientName = serviceKey;
        }

        if (string.IsNullOrWhiteSpace(options.DatabaseName))
        {
            var database = string.IsNullOrWhiteSpace(options.ClientName)
                ? services.GetService<IMongoDatabase>()
                : services.GetKeyedService<IMongoDatabase>(options.ClientName);
            if (database is not null)
            {
                options.DatabaseName = database.DatabaseNamespace.DatabaseName;
            }
        }
    }
}
