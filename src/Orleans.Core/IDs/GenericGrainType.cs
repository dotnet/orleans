using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Orleans.Serialization.TypeSystem;
using Orleans.Utilities;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a <see cref="GrainType"/> that is parameterized using type parameters.
    /// </summary>
    [Immutable]
    public readonly struct GenericGrainType : IEquatable<GenericGrainType>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GenericGrainType"/> struct.
        /// </summary>
        /// <param name="grainType">The underlying grain type.</param>
        /// <param name="arity">The generic arity of the grain type.</param>
        private GenericGrainType(GrainType grainType, int arity)
        {
            GrainType = grainType;
            Arity = arity;
        }

        /// <summary>
        /// The underlying grain type.
        /// </summary>
        public GrainType GrainType { get; }

        /// <summary>
        /// The generic arity of the grain type.
        /// </summary>
        public int Arity { get; }

        /// <summary>
        /// Returns <see langword="true" /> if this instance contains concrete type parameters.
        /// </summary>
        public bool IsConstructed => TypeConverterExtensions.IsConstructed(this.GrainType.Value);

        /// <summary>
        /// Returns the generic grain type corresponding to the provided value.
        /// </summary>
        public static bool TryParse(GrainType grainType, out GenericGrainType result)
        {
            var arity = TypeConverterExtensions.GetGenericTypeArity(grainType.Value);
            if (arity > 0)
            {
                result = new GenericGrainType(grainType, arity);
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Returns a non-constructed version of this instance.
        /// </summary>
        public GenericGrainType GetUnconstructedGrainType()
        {
            var generic = TypeConverterExtensions.GetDeconstructed(GrainType.Value);
            return new GenericGrainType(new GrainType(generic), Arity);
        }

        /// <summary>
        /// Returns a constructed version of this instance.
        /// </summary>
        public GenericGrainType Construct(TypeConverter formatter, params Type[] typeArguments)
        {
            if (Arity != typeArguments.Length)
            {
                ThrowIncorrectArgumentLength(typeArguments);
            }

            var constructed = formatter.GetConstructed(this.GrainType.Value, typeArguments);
            return new GenericGrainType(new GrainType(constructed), Arity);
        }

        /// <summary>
        /// Gets the type arguments using the provided type converter.
        /// </summary>
        /// <param name="converter">The type converter</param>
        /// <returns>The type arguments.</returns>
        public Type[] GetArguments(TypeConverter converter) => converter.GetArguments(this.GrainType.Value);

        /// <inheritdoc/>
        public override string ToString() => this.GrainType.ToString();

        /// <inheritdoc/>
        public bool Equals(GenericGrainType other) => this.GrainType.Equals(other.GrainType);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is GenericGrainType other && this.Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => this.GrainType.GetHashCode();

        [DoesNotReturn]
        private void ThrowIncorrectArgumentLength(Type[] typeArguments) => throw new ArgumentException($"Incorrect number of type arguments, {typeArguments.Length}, to construct a generic grain type with arity {Arity}.", nameof(typeArguments));
    }
}
