namespace Orleans.Runtime
{
    using System;

    /// <summary>
    /// Options for formatting type names.
    /// </summary>
    public sealed class TypeFormattingOptions : IEquatable<TypeFormattingOptions>
    {
        /// <summary>Initializes a new instance of <see cref="TypeFormattingOptions"/>.</summary>
        public TypeFormattingOptions(
            string nameSuffix = null,
            bool includeNamespace = true,
            bool includeGenericParameters = true,
            bool includeTypeParameters = true,
            char nestedClassSeparator = '.',
            bool includeGlobal = true)
        {

            this.NameSuffix = nameSuffix;
            this.IncludeNamespace = includeNamespace;
            this.IncludeGenericTypeParameters = includeGenericParameters;
            this.IncludeTypeParameters = includeTypeParameters;
            this.NestedTypeSeparator = nestedClassSeparator;
            this.IncludeGlobal = includeGlobal;
        }

        internal static TypeFormattingOptions Default { get; } = new TypeFormattingOptions();
        internal static TypeFormattingOptions LogFormat { get; } = new TypeFormattingOptions(includeGlobal: false);

        /// <summary>
        /// Gets a value indicating whether or not to include the fully-qualified namespace of the class in the result.
        /// </summary>
        public bool IncludeNamespace { get; }

        /// <summary>
        /// Gets a value indicating whether or not to include concrete type parameters in the result.
        /// </summary>
        public bool IncludeTypeParameters { get; }

        /// <summary>
        /// Gets a value indicating whether or not to include generic type parameters in the result.
        /// </summary>
        public bool IncludeGenericTypeParameters { get; }

        /// <summary>
        /// Gets the separator used between declaring types and their declared types.
        /// </summary>
        public char NestedTypeSeparator { get; }

        /// <summary>
        /// Gets the name to append to the formatted name, before any type parameters.
        /// </summary>
        public string NameSuffix { get; }

        /// <summary>
        /// Gets a value indicating whether or not to include the global namespace qualifier.
        /// </summary>
        public bool IncludeGlobal { get; }

        /// <summary>
        /// Indicates whether the current object is equal to another object of the same type.
        /// </summary>
        /// <param name="other">An object to compare with this object.</param>
        /// <returns>
        /// <see langword="true"/> if the specified object  is equal to the current object; otherwise, <see langword="false"/>.
        /// </returns>
        public bool Equals(TypeFormattingOptions other)
        {
            if (other is null)
            {
                return false;
            }
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            return this.IncludeNamespace == other.IncludeNamespace
                   && this.IncludeTypeParameters == other.IncludeTypeParameters
                   && this.IncludeGenericTypeParameters == other.IncludeGenericTypeParameters
                   && this.NestedTypeSeparator == other.NestedTypeSeparator
                   && string.Equals(this.NameSuffix, other.NameSuffix) && this.IncludeGlobal == other.IncludeGlobal;
        }

        /// <summary>
        /// Determines whether the specified object is equal to the current object.
        /// </summary>
        /// <param name="obj">The object to compare with the current object.</param>
        /// <returns>
        /// <see langword="true"/> if the specified object  is equal to the current object; otherwise, <see langword="false"/>.
        /// </returns>
        public override bool Equals(object obj) => Equals(obj as TypeFormattingOptions);

        /// <summary>
        /// Serves as a hash function for a particular type. 
        /// </summary>
        /// <returns>
        /// A hash code for the current object.
        /// </returns>
        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = this.IncludeNamespace.GetHashCode();
                hashCode = (hashCode * 397) ^ this.IncludeTypeParameters.GetHashCode();
                hashCode = (hashCode * 397) ^ this.IncludeGenericTypeParameters.GetHashCode();
                hashCode = (hashCode * 397) ^ this.NestedTypeSeparator.GetHashCode();
                hashCode = (hashCode * 397) ^ (this.NameSuffix != null ? this.NameSuffix.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ this.IncludeGlobal.GetHashCode();
                return hashCode;
            }
        }

        /// <summary>Determines whether the specified objects are equal.</summary>
        public static bool operator ==(TypeFormattingOptions left, TypeFormattingOptions right) => left?.Equals(right) ?? right is null;

        /// <summary>Determines whether the specified objects are not equal.</summary>
        public static bool operator !=(TypeFormattingOptions left, TypeFormattingOptions right) => !(left == right);
    }
}