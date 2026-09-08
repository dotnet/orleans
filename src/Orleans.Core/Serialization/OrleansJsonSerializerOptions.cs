using System;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Orleans.Serialization
{
    /// <summary>
    /// Configures Newtonsoft.Json serialization for Orleans framework types.
    /// </summary>
    public class OrleansJsonSerializerOptions
    {
        /// <summary>
        /// Gets or sets the serializer settings.
        /// </summary>
        /// <remarks>
        /// The settings are initialized with Orleans defaults and augmented with the Orleans serialization binder
        /// and converters during options post-configuration.
        /// </remarks>
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

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansJsonSerializerOptions"/> class using the default settings.
        /// </summary>
        public OrleansJsonSerializerOptions() => JsonSerializerSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings();
    }

    /// <summary>
    /// Configures <see cref="OrleansJsonSerializerOptions"/> with the Orleans serialization binder and converters.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve serializer dependencies.</param>
    public class ConfigureOrleansJsonSerializerOptions(IServiceProvider serviceProvider) : IPostConfigureOptions<OrleansJsonSerializerOptions>
    {
        /// <inheritdoc />
        public void PostConfigure(string? name, OrleansJsonSerializerOptions options)
        {
            OrleansJsonSerializerSettings.Configure(serviceProvider, options.JsonSerializerSettings, options.AllowAllTypes);
        }
    }
}
