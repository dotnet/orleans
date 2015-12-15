using Orleans.Providers.Streams.Common;

namespace Tester.TestStreamProviders.Generator
{
    /// <summary>
    /// This is a persistent stream provider that generates it's own events rather than reading them from storage.
    /// This is primarily for test purposes.
    /// </summary>
    public class GeneratorStreamProvider : PersistentStreamProvider<GeneratorAdapterFactory>
    {
    }
}
