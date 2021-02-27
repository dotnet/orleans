using Microsoft.Extensions.Options;

namespace Orleans.Serialization.Configuration
{
    /// <summary>
    /// Provides type manifest information.
    /// </summary>
    public interface ITypeManifestProvider : IConfigureOptions<TypeManifestOptions>
    {
    }
}