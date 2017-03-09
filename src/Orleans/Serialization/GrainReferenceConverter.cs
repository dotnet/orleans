using System;
using System.Collections.Concurrent;
using System.Reflection;
using Newtonsoft.Json;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Serialization
{
    internal class GrainReferenceConverter : JsonConverter
    {
        private static readonly Type GrainReferenceType;

        private static readonly ConcurrentDictionary<Type, GrainFactory.GrainReferenceCaster> Casters =
            new ConcurrentDictionary<Type, GrainFactory.GrainReferenceCaster>();
        private static readonly Func<Type, GrainFactory.GrainReferenceCaster> CreateCasterDelegate = CreateCaster;
        private readonly IGrainFactory grainFactory;

        public GrainReferenceConverter(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
        }

        static GrainReferenceConverter()
        {
            GrainReferenceType = typeof(GrainReference);
        }

        public override bool CanConvert(Type objectType)
        {
            return GrainReferenceType.IsAssignableFrom(objectType);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var key = (value as GrainReference)?.ToKeyString();
            serializer.Serialize(writer, key);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var key = serializer.Deserialize<string>(reader);
            if (string.IsNullOrWhiteSpace(key)) return null;

            var result = GrainReference.FromKeyString(key, null);
            this.grainFactory.BindGrainReference(result);
            return Casters.GetOrAdd(objectType, CreateCasterDelegate)(result);
        }

        private static GrainFactory.GrainReferenceCaster CreateCaster(Type grainReferenceType)
        {
            var interfaceType = grainReferenceType.GetTypeInfo().GetCustomAttribute<GrainReferenceAttribute>().TargetType;
            return GrainCasterFactory.CreateGrainReferenceCaster(interfaceType, grainReferenceType);
        }
    }
}