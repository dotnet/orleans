using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// The attribute which informs the code generator that code should be generated for this type.
    /// Forces generation of type serializer, throwing if a serializer could not be generated.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class GenerateSerializerAttribute : ConsiderForCodeGenerationAttribute
    {
        /// <summary>Initializes a new instance of <see cref="GenerateSerializerAttribute"/>.</summary>
        /// <param name="type">The type that the generator should generate code for</param>
        public GenerateSerializerAttribute(Type type) : base(type, true){ }
    }
}