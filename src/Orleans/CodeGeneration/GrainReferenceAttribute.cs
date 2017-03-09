using System;

namespace Orleans.CodeGeneration
{
    /// <summary>Identifies a concrete grain reference to an interface ID</summary>
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class GrainReferenceAttribute : GeneratedAttribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceAttribute"/> class.
        /// </summary>
        /// <param name="targetType">The type which this implementation applies to.</param>
        public GrainReferenceAttribute(Type targetType)
            : base(targetType)
        {
        }
    }
}