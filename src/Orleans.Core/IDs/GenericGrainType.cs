using System;
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
        private GenericGrainType(GrainType grainType)
        {
            GrainType = grainType;
        }

        /// <summary>
        /// The underlying grain type.
        /// </summary>
        public GrainType GrainType { get; }

        /// <summary>
        /// Returns <see langword="true" /> if this instance contains concrete type parameters.
        /// </summary>
        public bool IsConstructed => TypeConverterExtensions.IsConstructed(this.GrainType.Value);

        /// <summary>
        /// Returns the generic grain type corresponding to the provided value.
        /// </summary>
        public static bool TryParse(GrainType grainType, out GenericGrainType result)
        {
            if (TypeConverterExtensions.IsGenericType(grainType.Value))
            {
                result = new GenericGrainType(grainType);
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
            return new GenericGrainType(new GrainType(generic));
        }

        /// <summary>
        /// Returns a constructed version of this instance.
        /// </summary>
        public GenericGrainType Construct(TypeConverter formatter, params Type[] typeArguments)
        {
            var constructed = formatter.GetConstructed(this.GrainType.Value, typeArguments);
            return new GenericGrainType(new GrainType(constructed));
        }
        /// <summary>
        /// Returns the type arguments which this instance was constructed with.
        /// </summary>
        public Type[] GetArguments(TypeConverter formatter) => formatter.GetArguments(this.GrainType.Value);

        /// <inheritdoc/>
        public override string ToString() => this.GrainType.ToString();

        /// <inheritdoc/>
        public bool Equals(GenericGrainType other) => this.GrainType.Equals(other.GrainType);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is GenericGrainType other && this.Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => this.GrainType.GetHashCode();
    }
}
