namespace Orleans.CodeGeneration
{
    using System;

    /// <summary>
    /// The attribute which informs the code generator that code should be generated for this type.
    /// </summary>
    public class ConsiderForCodeGenerationAttribute : Attribute
    {
        /// <summary>Initializes a new instance of <see cref="ConsiderForCodeGenerationAttribute"/>.</summary>
        /// <param name="type">The type that the generator should generate code for</param>
        /// <param name="throwOnFailure">When <see langword="true"/>, it will throw an exception if code cannot be generated for this type.</param>
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
}
