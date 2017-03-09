using System;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters;
using Newtonsoft.Json;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    using Orleans.Providers;
    
    public class OrleansJsonSerializer : IExternalSerializer
    {
        public const string UseFullAssemblyNamesProperty = "UseFullAssemblyNames";
        public const string IndentJsonProperty = "IndentJSON";
        private readonly JsonSerializerSettings settings;
        private Logger logger;

        public OrleansJsonSerializer(SerializationManager serializationManager, IGrainFactory grainFactory)
        {
            this.settings = GetDefaultSerializerSettings(serializationManager, grainFactory);
        }

        /// <summary>
        /// Returns the default serializer settings.
        /// </summary>
        /// <returns>The default serializer settings.</returns>
        public static JsonSerializerSettings GetDefaultSerializerSettings(SerializationManager serializationManager, IGrainFactory grainFactory)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
#if !NETSTANDARD_TODO
                TypeNameAssemblyFormat = FormatterAssemblyStyle.Simple,

                // Types such as GrainReference need context during deserialization, so provide that context now.
                Context = new StreamingContext(StreamingContextStates.All, new SerializationContext(serializationManager)),
#endif
                Formatting = Formatting.None
            };

            settings.Converters.Add(new IPAddressConverter());
            settings.Converters.Add(new IPEndPointConverter());
            settings.Converters.Add(new GrainIdConverter());
            settings.Converters.Add(new SiloAddressConverter());
            settings.Converters.Add(new UniqueKeyConverter());
            settings.Converters.Add(new GrainReferenceConverter(grainFactory));

            return settings;
        }

        /// <summary>
        /// Customises the given serializer settings using provider configuration.
        /// Can be used by any provider, allowing the users to use a standard set of configuration attributes.
        /// </summary>
        /// <param name="settings">The settings to update.</param>
        /// <param name="config">The provider config.</param>
        /// <returns>The updated <see cref="JsonSerializerSettings" />.</returns>
        public static JsonSerializerSettings UpdateSerializerSettings(JsonSerializerSettings settings, IProviderConfiguration config)
        {
            if (config.Properties.ContainsKey(UseFullAssemblyNamesProperty))
            {
                bool useFullAssemblyNames;
                if (bool.TryParse(config.Properties[UseFullAssemblyNamesProperty], out useFullAssemblyNames) && useFullAssemblyNames)
                {
#if !NETSTANDARD_TODO
                    settings.TypeNameAssemblyFormat = FormatterAssemblyStyle.Full;
#endif
                }
            }

            if (config.Properties.ContainsKey(IndentJsonProperty))
            {
                bool indentJson;
                if (bool.TryParse(config.Properties[IndentJsonProperty], out indentJson) && indentJson)
                {
                    settings.Formatting = Formatting.Indented;
                }
            }
            return settings;
        }

        /// <inheritdoc />
        public void Initialize(Logger logger)
        {
            this.logger = logger;
        }

        /// <inheritdoc />
        public bool IsSupportedType(Type itemType)
        {
            return true;
        }

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context)
        {
            if (source == null)
            {
                return null;
            }

            var serializationContext = new SerializationContext(context.SerializationManager)
            {
                StreamWriter = new BinaryTokenStreamWriter()
            };
            
            Serialize(source, serializationContext, source.GetType());
            var deserializationContext = new DeserializationContext(context.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(serializationContext.StreamWriter.ToBytes())
            };

            var retVal = Deserialize(source.GetType(), deserializationContext);
            serializationContext.StreamWriter.ReleaseBuffers();
            return retVal;
        }

        /// <inheritdoc />
        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var reader = context.StreamReader;
            var str = reader.ReadString();
            return JsonConvert.DeserializeObject(str, expectedType, this.settings);
        }

        /// <summary>
        /// Serializes an object to a binary stream
        /// </summary>
        /// <param name="item">The object to serialize</param>
        /// <param name="context">The serialization context.</param>
        /// <param name="expectedType">The type the deserializer should expect</param>
        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var writer = context.StreamWriter;
            if (item == null)
            {
                writer.WriteNull();
                return;
            }

            var str = JsonConvert.SerializeObject(item, expectedType, this.settings);
            writer.Write(str);
        }
    }

#region JsonConverters

    #endregion
}
