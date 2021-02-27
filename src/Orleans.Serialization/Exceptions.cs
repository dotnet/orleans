using System;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    internal static class ExceptionHelper
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T ThrowArgumentOutOfRange<T>(string argument) => throw new ArgumentOutOfRangeException(argument);
    }

    [Serializable]
    [GenerateSerializer]
    public class SerializerException : Exception
    {
        public SerializerException()
        {
        }

        public SerializerException(string message) : base(message)
        {
        }

        public SerializerException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected SerializerException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class FieldIdNotPresentException : SerializerException
    {
        public FieldIdNotPresentException() : base("Attempted to access the field id from a tag which cannot have a field id.")
        {
        }

        protected FieldIdNotPresentException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class SchemaTypeInvalidException : SerializerException
    {
        public SchemaTypeInvalidException() : base("Attempted to access the schema type from a tag which cannot have a schema type.")
        {
        }

        protected SchemaTypeInvalidException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class FieldTypeInvalidException : SerializerException
    {
        public FieldTypeInvalidException() : base("Attempted to access the schema type from a tag which cannot have a schema type.")
        {
        }

        protected FieldTypeInvalidException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class FieldTypeMissingException : SerializerException
    {
        public FieldTypeMissingException(Type type) : base($"Attempted to deserialize an instance of abstract type {type}. No concrete type was provided.")
        {
        }

        protected FieldTypeMissingException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class ExtendedWireTypeInvalidException : SerializerException
    {
        public ExtendedWireTypeInvalidException() : base(
            "Attempted to access the extended wire type from a tag which does not have an extended wire type.")
        {
        }

        protected ExtendedWireTypeInvalidException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class UnsupportedWireTypeException : SerializerException
    {
        public UnsupportedWireTypeException()
        {
        }

        public UnsupportedWireTypeException(string message) : base(message)
        {
        }

        protected UnsupportedWireTypeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class ReferenceNotFoundException : SerializerException
    {
        [Id(0)]
        public uint TargetReference { get; }

        [Id(1)]
        public Type TargetReferenceType { get; }

        public ReferenceNotFoundException(Type targetType, uint targetId) : base(
            $"Reference with id {targetId} and type {targetType} not found.")
        {
            TargetReference = targetId;
            TargetReferenceType = targetType;
        }

        protected ReferenceNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            TargetReference = info.GetUInt32(nameof(TargetReference));
            TargetReferenceType = (Type)info.GetValue(nameof(TargetReferenceType), typeof(Type));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(TargetReference), TargetReference);
            info.AddValue(nameof(TargetReferenceType), TargetReferenceType);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class UnknownReferencedTypeException : SerializerException
    {
        public UnknownReferencedTypeException(uint reference) : base($"Unknown referenced type {reference}.")
        {
            Reference = reference;
        }

        protected UnknownReferencedTypeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            info.AddValue(nameof(Reference), Reference);
        }

        [Id(0)]
        public uint Reference { get; set; }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            Reference = info.GetUInt32(nameof(Reference));
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class UnknownWellKnownTypeException : SerializerException
    {
        public UnknownWellKnownTypeException(uint id) : base($"Unknown well-known type {id}.")
        {
            Id = id;
        }

        protected UnknownWellKnownTypeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            info.AddValue(nameof(Id), Id);
        }

        [Id(0)]
        public uint Id { get; set; }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            Id = info.GetUInt32(nameof(Id));
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class IllegalTypeException : SerializerException
    {
        public IllegalTypeException(string typeName) : base($"Type \"{typeName}\" is not allowed.")
        {
            TypeName = typeName;
        }

        protected IllegalTypeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            TypeName = info.GetString(nameof(TypeName));
        }

        [Id(0)]
        public string TypeName { get; }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(TypeName), TypeName);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class TypeMissingException : SerializerException
    {
        public TypeMissingException() : base("Expected a type but none were encountered.")
        {
        }

        protected TypeMissingException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class RequiredFieldMissingException : SerializerException
    {
        public RequiredFieldMissingException(string message) : base(message)
        {
        }

        protected RequiredFieldMissingException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class CodecNotFoundException : SerializerException
    {
        public CodecNotFoundException(string message) : base(message)
        {
        }

        protected CodecNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class UnexpectedLengthPrefixValueException : SerializerException
    {
        public UnexpectedLengthPrefixValueException(string message) : base(message)
        {
        }

        public UnexpectedLengthPrefixValueException(string typeName, uint expectedLength, uint actualLength)
            : base($"VarInt length specified in header for {typeName} should be {expectedLength} but is instead {actualLength}")
        {
        }

        protected UnexpectedLengthPrefixValueException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}