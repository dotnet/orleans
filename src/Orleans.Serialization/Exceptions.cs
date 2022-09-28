using System;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    internal static class ExceptionHelper
    {
        public static T ThrowArgumentOutOfRange<T>(string argument) => throw new ArgumentOutOfRangeException(argument);
    }

    /// <summary>
    /// Base exception for any serializer exception.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class SerializerException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerException"/> class.
        /// </summary>
        public SerializerException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public SerializerException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerException"/> class.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a null reference (<see langword="Nothing" /> in Visual Basic) if no inner exception is specified.</param>
        public SerializerException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        protected SerializerException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// An field identifier was expected but not present.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class FieldIdNotPresentException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FieldIdNotPresentException"/> class.
        /// </summary>
        public FieldIdNotPresentException() : base("Attempted to access the field id from a tag which cannot have a field id.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FieldIdNotPresentException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private FieldIdNotPresentException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// The schema type is invalid.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class SchemaTypeInvalidException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SchemaTypeInvalidException"/> class.
        /// </summary>
        public SchemaTypeInvalidException() : base("Attempted to access the schema type from a tag which cannot have a schema type.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SchemaTypeInvalidException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private SchemaTypeInvalidException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// The field type is invalid.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class FieldTypeInvalidException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FieldTypeInvalidException"/> class.
        /// </summary>
        public FieldTypeInvalidException() : base("Attempted to access the schema type from a tag which cannot have a schema type.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FieldTypeInvalidException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private FieldTypeInvalidException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// A field type was expected but not present.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class FieldTypeMissingException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FieldTypeMissingException"/> class.
        /// </summary>
        /// <param name="type">The type.</param>
        public FieldTypeMissingException(Type type) : base($"Attempted to deserialize an instance of abstract type {type}. No concrete type was provided.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FieldTypeMissingException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private FieldTypeMissingException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// The extended wire type is invalid.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class ExtendedWireTypeInvalidException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ExtendedWireTypeInvalidException"/> class.
        /// </summary>
        public ExtendedWireTypeInvalidException() : base(
            "Attempted to access the extended wire type from a tag which does not have an extended wire type.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ExtendedWireTypeInvalidException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private ExtendedWireTypeInvalidException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// The wire type is unsupported.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class UnsupportedWireTypeException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnsupportedWireTypeException"/> class.
        /// </summary>
        public UnsupportedWireTypeException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnsupportedWireTypeException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public UnsupportedWireTypeException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnsupportedWireTypeException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private UnsupportedWireTypeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// A referenced value was not found.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class ReferenceNotFoundException : SerializerException
    {
        /// <summary>
        /// Gets the target reference.
        /// </summary>
        /// <value>The target reference.</value>
        [Id(0)]
        public uint TargetReference { get; }

        /// <summary>
        /// Gets the type of the target reference.
        /// </summary>
        /// <value>The type of the target reference.</value>
        [Id(1)]
        public Type TargetReferenceType { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ReferenceNotFoundException"/> class.
        /// </summary>
        /// <param name="targetType">Type of the target.</param>
        /// <param name="targetId">The target identifier.</param>
        public ReferenceNotFoundException(Type targetType, uint targetId) : base(
            $"Reference with id {targetId} and type {targetType} not found.")
        {
            TargetReference = targetId;
            TargetReferenceType = targetType;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ReferenceNotFoundException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private ReferenceNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            TargetReference = info.GetUInt32(nameof(TargetReference));
            TargetReferenceType = (Type)info.GetValue(nameof(TargetReferenceType), typeof(Type));
        }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(TargetReference), TargetReference);
            info.AddValue(nameof(TargetReferenceType), TargetReferenceType);
        }
    }

    /// <summary>
    /// A referenced type was not found.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class UnknownReferencedTypeException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnknownReferencedTypeException"/> class.
        /// </summary>
        /// <param name="reference">The reference.</param>
        public UnknownReferencedTypeException(uint reference) : base($"Unknown referenced type {reference}.")
        {
            Reference = reference;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnknownReferencedTypeException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private UnknownReferencedTypeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            info.AddValue(nameof(Reference), Reference);
        }

        /// <summary>
        /// Gets or sets the reference.
        /// </summary>
        /// <value>The reference.</value>
        [Id(0)]
        public uint Reference { get; set; }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            Reference = info.GetUInt32(nameof(Reference));
        }
    }

    /// <summary>
    /// A well-known type was not known.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class UnknownWellKnownTypeException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnknownWellKnownTypeException"/> class.
        /// </summary>
        /// <param name="id">The identifier.</param>
        public UnknownWellKnownTypeException(uint id) : base($"Unknown well-known type {id}.")
        {
            Id = id;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnknownWellKnownTypeException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private UnknownWellKnownTypeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            info.AddValue(nameof(Id), Id);
        }

        /// <summary>
        /// Gets or sets the identifier.
        /// </summary>
        /// <value>The identifier.</value>
        [Id(0)]
        public uint Id { get; set; }

        /// <inheritdoc />
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            Id = info.GetUInt32(nameof(Id));
        }
    }

    /// <summary>
    /// A specified type is not allowed.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class IllegalTypeException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="IllegalTypeException"/> class.
        /// </summary>
        /// <param name="typeName">Name of the type.</param>
        public IllegalTypeException(string typeName) : base($"Type \"{typeName}\" is not allowed.")
        {
            TypeName = typeName;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IllegalTypeException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private IllegalTypeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            TypeName = info.GetString(nameof(TypeName));
        }

        /// <summary>
        /// Gets the name of the type.
        /// </summary>
        /// <value>The name of the type.</value>
        [Id(0)]
        public string TypeName { get; }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(TypeName), TypeName);
        }
    }

    /// <summary>
    /// A type was expected but not found.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class TypeMissingException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TypeMissingException"/> class.
        /// </summary>
        public TypeMissingException() : base("Expected a type but none were encountered.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TypeMissingException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private TypeMissingException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// A required field was not present.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class RequiredFieldMissingException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RequiredFieldMissingException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public RequiredFieldMissingException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RequiredFieldMissingException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private RequiredFieldMissingException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// No suitable serializer codec was found for a specified type.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class CodecNotFoundException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CodecNotFoundException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public CodecNotFoundException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CodecNotFoundException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private CodecNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }

    /// <summary>
    /// A length encoded field which is expected to have a length
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class UnexpectedLengthPrefixValueException : SerializerException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnexpectedLengthPrefixValueException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public UnexpectedLengthPrefixValueException(string message) : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnexpectedLengthPrefixValueException"/> class.
        /// </summary>
        /// <param name="typeName">Name of the type.</param>
        /// <param name="expectedLength">The expected length.</param>
        /// <param name="actualLength">The actual length.</param>
        public UnexpectedLengthPrefixValueException(string typeName, uint expectedLength, uint actualLength)
            : base($"VarInt length specified in header for {typeName} should be {expectedLength} but is instead {actualLength}")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UnexpectedLengthPrefixValueException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="T:System.Runtime.Serialization.SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="T:System.Runtime.Serialization.StreamingContext" /> that contains contextual information about the source or destination.</param>
        private UnexpectedLengthPrefixValueException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}