using System;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Orleans.Serialization;

public class OrleansJsonSerializerOptions
{
    public JsonSerializerSettings JsonSerializerSettings { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether any resolvable type may be constructed during
    /// deserialization, including types named in the serialized payload via
    /// <see cref="Newtonsoft.Json.TypeNameHandling"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default value is <see langword="false"/>. When <see langword="false"/>, only types permitted
    /// by the Orleans type allow-list may be constructed during deserialization &#8212; for example,
    /// types marked with <c>[GenerateSerializer]</c>, types added to
    /// <see cref="Orleans.Serialization.Configuration.TypeManifestOptions.AllowedTypes"/>, or types
    /// allowed by a registered <see cref="ITypeNameFilter"/> or <see cref="ITypeFilter"/>. This prevents
    /// arbitrary, potentially dangerous types from being instantiated when deserializing untrusted
    /// persisted or streamed state.
    /// </para>
    /// <para>
    /// Setting this to <see langword="true"/> restores the previous behavior of allowing any loadable
    /// type to be constructed during deserialization. This is <b>insecure</b> and is not recommended;
    /// prefer allow-listing individual types instead.
    /// </para>
    /// </remarks>
    public bool AllowAllTypes { get; set; }

    public OrleansJsonSerializerOptions() => JsonSerializerSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings();
}

public class ConfigureOrleansJsonSerializerOptions(IServiceProvider serviceProvider) : IPostConfigureOptions<OrleansJsonSerializerOptions>
{
    public void PostConfigure(string? name, OrleansJsonSerializerOptions options)
    {
        OrleansJsonSerializerSettings.Configure(serviceProvider, options.JsonSerializerSettings, options.AllowAllTypes);
    }
}
