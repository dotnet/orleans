namespace Orleans.Serialization
{
    using System;
    using System.Reflection;

    internal static class IlBasedSerializerRegistry
    {
        private static readonly IlBasedSerializers Serializers;

        static IlBasedSerializerRegistry()
        {
            Serializers = new IlBasedSerializers();
        }

        public static void RegisterAll(params string[] types)
        {
            for (var i = 0; i < types.Length; i++)
            {
                var type = Type.GetType(types[i]);
                if (type == null) continue;
                SerializationManager.FindSerializationInfo(type);
                if (!SerializationManager.HasSerializer(type) && IlBasedSerializerTypeChecker.IsSupportedType(type.GetTypeInfo()))
                {
                    Serializers.GetAndRegister(type);
                }
            }
        }
    }
}