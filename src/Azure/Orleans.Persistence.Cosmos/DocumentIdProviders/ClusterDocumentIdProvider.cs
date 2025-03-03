namespace Orleans.Persistence.Cosmos.DocumentIdProviders;

public sealed class ClusterDocumentIdProvider : DocumentIdProviderBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterDocumentIdProvider"/> class.
    /// </summary>
    /// <param name="options">The cluster options</param>
    public ClusterDocumentIdProvider(IOptions<ClusterOptions> options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        this.serviceId = options.Value?.ServiceId;
    }
}
