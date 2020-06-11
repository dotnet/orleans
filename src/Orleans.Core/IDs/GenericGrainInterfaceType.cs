using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a <see cref="GrainInterfaceType"/> that is parameterized using type parameters.
    /// </summary>
    public readonly struct GenericGrainInterfaceType
    {
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
        public bool IsConstructed => TypeConverterExtensions.IsConstructed(this.Value.ToStringUtf8());

        /// <summary>
        /// Returns the generic interface id corresponding to the provided value.
        /// </summary>
        public static bool TryParse(GrainInterfaceType grainType, out GenericGrainInterfaceType result)
        {
            if (!grainType.IsDefault && TypeConverterExtensions.IsGenericType(grainType.ToStringUtf8()))
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
            var str = this.Value.ToStringUtf8();
            var generic = TypeConverterExtensions.GetDeconstructed(str);
            return new GenericGrainInterfaceType(GrainInterfaceType.Create(generic));
        }

        /// <summary>
        /// Returns a constructed version of this instance.
        /// </summary>
        public GenericGrainInterfaceType Construct(TypeConverter formatter, params Type[] typeArguments)
        {
            var constructed = formatter.GetConstructed(this.Value.ToStringUtf8(), typeArguments);
            return new GenericGrainInterfaceType(GrainInterfaceType.Create(constructed));
        }

        /// <summary>
        /// Returns the type arguments which this instance was constructed with.
        /// </summary>
        public Type[] GetArguments(TypeConverter formatter) => formatter.GetArguments(this.Value.ToStringUtf8());

        /// <summary>
        /// Returns the string form of the type arguments which this instance was constructed with.
        /// </summary>
        public string GetArgumentsString() => TypeConverterExtensions.GetArgumentsString(this.Value.ToStringUtf8());

        /// <inheritdoc/>
        public override string ToString() => this.Value.ToString();
    }
}
