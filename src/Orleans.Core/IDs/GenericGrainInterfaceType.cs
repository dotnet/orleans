using System;
using System.Buffers.Text;
using Orleans.Serialization.TypeSystem;
using Orleans.Utilities;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a <see cref="GrainInterfaceType"/> that is parameterized using type parameters.
    /// </summary>
    [Immutable]
    public readonly struct GenericGrainInterfaceType
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GenericGrainInterfaceType"/> struct.
        /// </summary>
        /// <param name="value">The underlying grain interface type.</param>
        private GenericGrainInterfaceType(GrainInterfaceType value)
        {
            Value = value;
        }

        /// <summary>
        /// The underlying <see cref="GrainInterfaceType"/>
        /// </summary>
        public GrainInterfaceType Value { get; }

        /// <summary>
        /// Returns <see langword="true" /> if this instance contains concrete type parameters.
        /// </summary>
        public bool IsConstructed => TypeConverterExtensions.IsConstructed(this.Value.Value);

        /// <summary>
        /// Returns the generic interface id corresponding to the provided value.
        /// </summary>
        public static bool TryParse(GrainInterfaceType grainType, out GenericGrainInterfaceType result)
        {
            if (!grainType.IsDefault && TypeConverterExtensions.IsGenericType(grainType.Value))
            {
                result = new GenericGrainInterfaceType(grainType);
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Returns a non-constructed version of this instance.
        /// </summary>
        public GenericGrainInterfaceType GetGenericGrainType()
        {
            var generic = TypeConverterExtensions.GetDeconstructed(Value.Value);
            return new GenericGrainInterfaceType(new GrainInterfaceType(generic));
        }

        /// <summary>
        /// Returns a constructed version of this instance.
        /// </summary>
        public GenericGrainInterfaceType Construct(TypeConverter formatter, params Type[] typeArguments)
        {
            var constructed = formatter.GetConstructed(this.Value.Value, typeArguments);
            return new GenericGrainInterfaceType(new GrainInterfaceType(constructed));
        }

        /// <summary>
        /// Returns the type arguments which this instance was constructed with.
        /// </summary>
        public Type[] GetArguments(TypeConverter formatter) => formatter.GetArguments(this.Value.Value);

        /// <summary>
        /// Returns a UTF8 interpretation of the current instance.
        /// </summary>
        public override string ToString() => Value.ToString();
    }
}
