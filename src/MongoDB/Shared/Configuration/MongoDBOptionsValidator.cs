using Microsoft.Extensions.Options;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.MongoDB;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.MongoDB;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.MongoDB;
#else
#error Define a MongoDB provider namespace.
#endif

internal sealed class MongoDBOptionsValidator<T> : IConfigurationValidator where T : MongoDBOptions, new()
{
    private readonly T options;

    public MongoDBOptionsValidator(IOptions<T> options)
    {
        this.options = options.Value;
    }

    public void ValidateConfiguration()
    {
        options.Validate();
    }
}
