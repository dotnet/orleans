namespace Orleans.CodeGeneration
{
    using System;

    /// <summary>
    /// The attribute which informs the code generator that code should be generated for this type.
    /// </summary>
    public class ConsiderForCodeGenerationAttribute : Attribute
    {
        protected ConsiderForCodeGenerationAttribute(Type type, bool throwOnFailure = false)
        {
            this.Type = type;
            this.ThrowOnFailure = throwOnFailure;
        }

        /// <summary>
        /// Gets the type which should be considered for code generation.
        /// </summary>
        public Type Type { get; private set; }

        /// <summary>
        /// Gets a value indicating whether or not to throw if code was not generated for the specified type.
        /// </summary>
        public bool ThrowOnFailure { get; private set; }
    }

    /// <summary>
    /// The attribute which informs the code generator that code should be generated for this type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class KnownTypeAttribute : ConsiderForCodeGenerationAttribute
    {
        public KnownTypeAttribute(Type type) : base(type){}
    }

    /// <summary>
    /// The attribute which informs the code generator that code should be generated for this type.
    /// Forces generation of type serializer, throwing if a serializer could not be generated.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class GenerateSerializerAttribute : ConsiderForCodeGenerationAttribute
    {
        public GenerateSerializerAttribute(Type type) : base(type, true){ }
    }
}
