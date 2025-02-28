using System;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.GrainReferences;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization
{
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
        /// Returns the default serializer settings.
        /// </summary>
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <returns>The default serializer settings.</returns>
        public static JsonSerializerSettings GetDefaultSerializerSettings(IServiceProvider services)
        {
            var settings = GetDefaultSerializerSettings();
            Configure(services, settings);
            return settings;
        }

        internal static void Configure(IServiceProvider services, JsonSerializerSettings jsonSerializerSettings)
        {
            if (jsonSerializerSettings.SerializationBinder == null)
            {
                var typeResolver = services.GetRequiredService<TypeResolver>();
                jsonSerializerSettings.SerializationBinder = new OrleansJsonSerializationBinder(typeResolver);
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
        /// <param name="useFullAssemblyNames">if set to <c>true</c>, use full assembly-qualified names when formatting type names.</param>
        /// <param name="indentJson">if set to <c>true</c>, indent the formatted JSON.</param>
        /// <param name="typeNameHandling">The type name handling options.</param>
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