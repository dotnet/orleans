using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// The attribute which informs the code generator that code should be generated for this type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class KnownTypeAttribute : ConsiderForCodeGenerationAttribute
    {
        /// <summary>Initializes a new instance of <see cref="KnownAssemblyAttribute"/>.</summary>
        /// <param name="type">The type that the generator should generate code for</param>
        public KnownTypeAttribute(Type type) : base(type){}
    }
}