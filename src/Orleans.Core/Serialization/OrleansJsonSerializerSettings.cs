using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.GrainReferences;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization
{
    /// <summary>
    /// Provides factory and configuration methods for Newtonsoft.Json settings which support Orleans framework types.
    /// </summary>
    public static class OrleansJsonSerializerSettings
    {
        internal static JsonSerializerSettings GetDefaultSerializerSettings()
        {
            return new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
                Formatting = Formatting.None,
                SerializationBinder = null,
            };
        }

        /// <summary>
        /// Creates the default serializer settings and configures the Orleans serialization binder and converters.
        /// </summary>
        /// <param name="services">The service provider used to resolve serializer dependencies.</param>
        /// <returns>The configured default serializer settings.</returns>
        public static JsonSerializerSettings GetDefaultSerializerSettings(IServiceProvider services)
        {
            var settings = GetDefaultSerializerSettings();
            var allowAllTypes = services.GetService<IOptions<OrleansJsonSerializerOptions>>()?.Value.AllowAllTypes ?? false;
            Configure(services, settings, allowAllTypes);
            return settings;
        }

        internal static void Configure(IServiceProvider services, JsonSerializerSettings jsonSerializerSettings, bool allowAllTypes = false)
        {
            if (jsonSerializerSettings.SerializationBinder == null)
            {
                var typeResolver = services.GetRequiredService<TypeResolver>();
                var typeConverter = services.GetRequiredService<TypeConverter>();
                jsonSerializerSettings.SerializationBinder = new OrleansJsonSerializationBinder(typeConverter, typeResolver, allowAllTypes);
            }

            jsonSerializerSettings.Converters.Add(new IPAddressConverter());
            jsonSerializerSettings.Converters.Add(new IPEndPointConverter());
            jsonSerializerSettings.Converters.Add(new GrainIdConverter());
            jsonSerializerSettings.Converters.Add(new ActivationIdConverter());
            jsonSerializerSettings.Converters.Add(new SiloAddressJsonConverter());
            jsonSerializerSettings.Converters.Add(new MembershipVersionJsonConverter());
            jsonSerializerSettings.Converters.Add(new UniqueKeyConverter());
            jsonSerializerSettings.Converters.Add(new GrainReferenceJsonConverter(services.GetRequiredService<GrainReferenceActivator>()));
        }

        /// <summary>
        /// Updates the provided serializer settings with the specified options.
        /// </summary>
        /// <param name="settings">The settings.</param>
        /// <param name="useFullAssemblyNames">
        /// When <see langword="true"/>, use full assembly-qualified names when formatting type names;
        /// otherwise, leave the current assembly name formatting unchanged.
        /// </param>
        /// <param name="indentJson">
        /// When <see langword="true"/>, indent the formatted JSON; otherwise, leave the current formatting unchanged.
        /// </param>
        /// <param name="typeNameHandling">
        /// The type name handling value to apply, or <see langword="null"/> to leave the current value unchanged.
        /// </param>
        /// <returns>The provided serializer settings.</returns>
        public static JsonSerializerSettings UpdateSerializerSettings(JsonSerializerSettings settings, bool useFullAssemblyNames, bool indentJson, TypeNameHandling? typeNameHandling)
        {
            if (useFullAssemblyNames)
            {
                settings.TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full;
            }

            if (indentJson)
            {
                settings.Formatting = Formatting.Indented;
            }

            if (typeNameHandling.HasValue)
            {
                settings.TypeNameHandling = typeNameHandling.Value;
            }

            return settings;
        }
    }
}