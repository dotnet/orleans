using Microsoft.Extensions.Options;

namespace Orleans.Providers.MongoDB.Configuration;

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
