using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    internal static class DotNetSerializableUtilities
    {
        private static readonly Type[] SerializationConstructorParameterTypes = { typeof(SerializationInfo), typeof(StreamingContext) };

        public static bool HasSerializationConstructor(Type type)
        {
            return type.GetConstructor(
                       BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                       null,
                       SerializationConstructorParameterTypes,
                       null) != null;
        }

        public static bool HasSerializationHookAttributes(Type type)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                       .Any(m => m.GetCustomAttributes().Any(a =>
                           a is OnSerializingAttribute ||
                           a is OnSerializedAttribute ||
                           a is OnDeserializingAttribute ||
                           a is OnDeserializedAttribute));
        }
    }
}