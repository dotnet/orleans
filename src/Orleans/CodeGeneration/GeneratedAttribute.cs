using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// For internal (run-time) use only.
    /// Base class of all the activation attributes 
    /// </summary>
    [AttributeUsage(System.AttributeTargets.All)]
    public abstract class GeneratedAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GeneratedAttribute"/> class.
        /// </summary>
        /// <param name="targetType">The type which this implementation applies to.</param>
        protected GeneratedAttribute(Type targetType)
        {
            this.TargetType = targetType;
        }

        /// <summary>
        /// Gets the type which this implementation applies to.
        /// </summary>
        public Type TargetType { get; }
    }
}
