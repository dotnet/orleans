using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a <see cref="GrainInterfaceId"/> that is parameterized using type parameters.
    /// </summary>
    public readonly struct GenericGrainInterfaceId
    {
        private GenericGrainInterfaceId(GrainInterfaceId value)
        {
            Value = value;
        }

        /// <summary>
        /// The underlying <see cref="GrainInterfaceId"/>
        /// </summary>
        public GrainInterfaceId Value { get; }

        /// <summary>
        /// Returns <see langword="true" /> if this instance contains concrete type parameters.
        /// </summary>
        public bool IsConstructed => TypeConverterExtensions.IsConstructed(this.Value.ToStringUtf8());

        /// <summary>
        /// Returns the generic interface id corresponding to the provided value.
        /// </summary>
        public static bool TryParse(GrainInterfaceId grainType, out GenericGrainInterfaceId result)
        {
            if (!grainType.IsDefault && TypeConverterExtensions.IsGenericType(grainType.ToStringUtf8()))
            {
                result = new GenericGrainInterfaceId(grainType);
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Returns a non-constructed version of this instance.
        /// </summary>
        public GenericGrainInterfaceId GetGenericGrainType()
        {
            var str = this.Value.ToStringUtf8();
            var generic = TypeConverterExtensions.GetDeconstructed(str);
            return new GenericGrainInterfaceId(GrainInterfaceId.Create(generic));
        }

        /// <summary>
        /// Returns a constructed version of this instance.
        /// </summary>
        public GenericGrainInterfaceId Construct(TypeConverter formatter, params Type[] typeArguments)
        {
            var constructed = formatter.GetConstructed(this.Value.ToStringUtf8(), typeArguments);
            return new GenericGrainInterfaceId(GrainInterfaceId.Create(constructed));
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
