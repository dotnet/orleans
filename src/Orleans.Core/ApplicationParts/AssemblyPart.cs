using System;
using System.Diagnostics;
using System.Reflection;

namespace Orleans.ApplicationParts
{
    /// <summary>
    /// An <see cref="IApplicationPart"/> backed by an <see cref="Assembly"/>.
    /// </summary>
    [DebuggerDisplay("{" + nameof(Assembly) + "}")]
    public class AssemblyPart : IApplicationPart
    {
        /// <summary>
        /// Initalizes a new <see cref="AssemblyPart"/> instance.
        /// </summary>
        /// <param name="assembly"></param>
        public AssemblyPart(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            this.Assembly = assembly;
        }

        /// <summary>
        /// Gets the <see cref="Assembly"/> of the <see cref="IApplicationPart"/>.
        /// </summary>
        public Assembly Assembly { get; }
        
        /// <summary>
        /// Returns <see langword="true"/> if this instance is equivalent to the provided instance, <see langword="false"/> otherwise.
        /// </summary>
        /// <param name="other">The other instance/</param>
        /// <returns>
        /// <see langword="true"/> if this instance is equivalent to the provided instance, <see langword="false"/> otherwise.
        /// </returns>
        protected bool Equals(AssemblyPart other)
        {
            return Equals(this.Assembly, other.Assembly);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == this.GetType() && this.Equals((AssemblyPart) obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return this.Assembly != null ? this.Assembly.GetHashCode() : 0;
        }
    }
}
