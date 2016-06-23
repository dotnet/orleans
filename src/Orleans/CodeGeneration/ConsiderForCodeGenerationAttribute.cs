namespace Orleans.CodeGeneration
{
    using System;
    
    /// <summary>
    /// Abstract base class for attributes that effect code generation of types.
    /// </summary>
    public abstract class ConsiderForCodeGenerationAttribute : Attribute
    {
        protected ConsiderForCodeGenerationAttribute(Type type, bool generateSerializer)
        {
            this.Type = type;
            this.GenerateSerializer = generateSerializer;
        }

        public Type Type { get; private set; }
        public bool GenerateSerializer { get; private set; }
    }

    /// <summary>
    /// The attribute which informs the code generator that code should be generated for this type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class KnownTypeAttribute : ConsiderForCodeGenerationAttribute
    {
        public KnownTypeAttribute(Type type) : base(type, false){}
    }

    /// <summary>
    /// The attribute which informs the code generator that code should be generated for this type.
    /// Forces generation of type serializer.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class GenerateSerializerAttribute : ConsiderForCodeGenerationAttribute
    {
        public GenerateSerializerAttribute(Type type) : base(type, true){ }
    }
}
