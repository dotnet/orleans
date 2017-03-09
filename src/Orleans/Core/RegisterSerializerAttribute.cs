using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Used to make a class for auto-registration as a serialization helper.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    [Obsolete("[RegisterSerializer] is obsolete, please use [Serializer(typeof(TargetType))] instead. Note that the signature of Register has changed to 'void Register(SerializationManager sm)'.")]
    public sealed class RegisterSerializerAttribute : Attribute
    {
    }
}