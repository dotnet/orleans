using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    public class BinaryFormatterSerializer : IExternalSerializer
    {
        private readonly IServiceProvider _serviceProvider;
        private BinaryFormatterGrainReferenceSurrogateSelector _grainReferenceSurrogateSelector;

        public BinaryFormatterSerializer(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        private BinaryFormatter GetBinaryFormatter(object additionalContext)
        {
            return new BinaryFormatter
            {
                Context = new StreamingContext(StreamingContextStates.All, additionalContext),
                SurrogateSelector = _grainReferenceSurrogateSelector ??= _serviceProvider.GetRequiredService<BinaryFormatterGrainReferenceSurrogateSelector>()
            };
        }

        public bool IsSupportedType(Type itemType)
        {
            return itemType.IsSerializable;
        }

        public object DeepCopy(object source, ICopyContext context)
        {
            if (source == null)
            {
                return null;
            }

            var formatter = this.GetBinaryFormatter(context); 
            object ret = null;
            using (var memoryStream = new MemoryStream())
            {
                formatter.Serialize(memoryStream, source);
                memoryStream.Flush();
                memoryStream.Seek(0, SeekOrigin.Begin);
                formatter.Binder = DynamicBinder.Instance;
                ret = formatter.Deserialize(memoryStream);
            }

            return ret;
        }

        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            var writer = context.StreamWriter;
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            if (item == null)
            {
                writer.WriteNull();
                return;
            }

            var formatter = this.GetBinaryFormatter(context);
            byte[] bytes;
            using (var memoryStream = new MemoryStream())
            {
                formatter.Serialize(memoryStream, item);
                memoryStream.Flush();
                bytes = memoryStream.ToArray();
            }
            
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            var reader = context.StreamReader;
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var n = reader.ReadInt();
            var bytes = reader.ReadBytes(n);
            var formatter = this.GetBinaryFormatter(context);

            object retVal = null;
            using (var memoryStream = new MemoryStream(bytes))
            {
                retVal = formatter.Deserialize(memoryStream);
            }

            return retVal;
        }


        /// <summary>
        /// This appears necessary because the BinaryFormatter by default will not see types
        /// that are defined by the InvokerGenerator.
        /// Needs to be public since it used by generated client code.
        /// </summary>
        class DynamicBinder : SerializationBinder
        {
            public static readonly SerializationBinder Instance = new DynamicBinder();

            private readonly CachedTypeResolver typeResolver = new CachedTypeResolver();
            private readonly Dictionary<string, Assembly> assemblies = new Dictionary<string, Assembly>();

            public override Type BindToType(string assemblyName, string typeName)
            {
                var fullName = !string.IsNullOrWhiteSpace(assemblyName) ? typeName + ',' + assemblyName : typeName;
                if (typeResolver.TryResolveType(fullName, out var type)) return type;

                lock (this.assemblies)
                {
                    Assembly result;
                    if (!this.assemblies.TryGetValue(assemblyName, out result))
                    {
                        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                            this.assemblies[assembly.GetName().FullName] = assembly;

                        // in some cases we have to explicitly load the assembly even though it seems to be already loaded but for some reason it's not listed in AppDomain.CurrentDomain.GetAssemblies()
                        if (!this.assemblies.TryGetValue(assemblyName, out result))
                            this.assemblies[assemblyName] = Assembly.Load(new AssemblyName(assemblyName));

                        result = this.assemblies[assemblyName];
                    }

                    return result.GetType(typeName);
                }
            }
        }
    }
}