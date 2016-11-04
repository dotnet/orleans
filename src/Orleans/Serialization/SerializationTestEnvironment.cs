using System.Collections.Generic;
using System.Reflection;

using Orleans.Runtime;

namespace Orleans.Serialization
{
    public class SerializationTestEnvironment
    {
        public SerializationTestEnvironment()
        {
            this.TypeMetadataCache = new TypeMetadataCache();
            this.AssemblyProcessor = new AssemblyProcessor(this.TypeMetadataCache);
            this.GrainFactory = new GrainFactory(null, this.TypeMetadataCache);
        }

        public void InitializeForTesting(List<TypeInfo> serializationProviders = null, TypeInfo fallbackType = null)
        {
            SerializationManager.InitializeForTesting(serializationProviders, fallbackType);
            this.AssemblyProcessor.Initialize();
        }

        public static SerializationTestEnvironment Initialize(List<TypeInfo> serializationProviders = null, TypeInfo fallbackType = null)
        {
            var result = new SerializationTestEnvironment();
            result.InitializeForTesting(serializationProviders, fallbackType);
            return result;
        }

        internal AssemblyProcessor AssemblyProcessor { get; set; }
        internal TypeMetadataCache TypeMetadataCache { get; set; }
        public IGrainFactory GrainFactory { get; set; }
    }
}