using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Uniquely identifies a grain interface.
    /// </summary>
    [Serializable, Immutable]
    [GenerateSerializer]
    public struct GrainInterfaceType : IEquatable<GrainInterfaceType>
    {
        /// <summary>
        /// The underlying value.
        /// </summary>
        [Id(1)]
        private readonly IdSpan _value;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceType"/> instance.
        /// </summary>
        public GrainInterfaceType(string value) => _value = IdSpan.Create(value);

        /// <summary>
        /// Creates a <see cref="GrainInterfaceType"/> instance.
        /// </summary>
        public GrainInterfaceType(IdSpan value) => _value = value;

        /// <summary>
        /// Returns the <see cref="IdSpan"/> value underlying this instance.
        /// </summary>
        public IdSpan Value => _value;

        /// <summary>
        /// Returns true if this value is equal to the <see langword="default"/> instance.
        /// </summary>
        public bool IsDefault => _value.IsDefault;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceType"/> instance.
        /// </summary>
        public static GrainInterfaceType Create(string value) => new GrainInterfaceType(value);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is GrainInterfaceType id && this.Equals(id);

        /// <inheritdoc />
        public bool Equals(GrainInterfaceType other) => _value.Equals(other._value);

        /// <inheritdoc />
        public override int GetHashCode() => _value.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => _value.ToStringUtf8();

        /// <summary>
        /// Returns a UTF8 interpretation of the current instance.
        /// </summary>
        /// <returns></returns>
        public string ToStringUtf8() => _value.ToStringUtf8();

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(GrainInterfaceType left, GrainInterfaceType right) => left.Equals(right);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(GrainInterfaceType left, GrainInterfaceType right) => !left.Equals(right);
    }

    /// <summary>
    /// Gets a <see cref="GrainInterfaceType"/> for an interface.
    /// </summary>
    public interface IGrainInterfaceTypeProvider
    {
        /// <summary>
        /// Gets the <see cref="GrainInterfaceType"/> corresponding to the specified <paramref name="type"/>.
        /// </summary>
        /// <param name="type">The grain interface type instance.</param>
        /// <param name="grainInterfaceType">The resulting grain interface type identifier.</param>
        /// <returns>
        /// <see langword="true"/> if a <see cref="GrainInterfaceType"/> corresponding to the provided type was found, otherwise <see langword="false"/>.
        /// </returns>
        bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType grainInterfaceType);
    }

    /// <summary>
    /// Gets a <see cref="GrainInterfaceType"/> from attributes implementing <see cref="IGrainInterfaceTypeProviderAttribute"/>.
    /// </summary>
    public class AttributeGrainInterfaceTypeProvider : IGrainInterfaceTypeProvider
    {
        /// <summary>
        /// The service provider.
        /// </summary>
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Creates a <see cref="AttributeGrainInterfaceTypeProvider"/> instance.
        /// </summary>
        public AttributeGrainInterfaceTypeProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType grainInterfaceType)
        {
            foreach (var attr in type.GetCustomAttributes(inherit: false))
            {
                if (attr is IGrainInterfaceTypeProviderAttribute provider)
                {
                    grainInterfaceType = provider.GetGrainInterfaceType(this._serviceProvider, type);
                    return true;
                }
            }

            grainInterfaceType = default;
            return false;
        }
    }

    /// <summary>
    /// An <see cref="Attribute"/> which implements this specifies the <see cref="GrainInterfaceType"/> of the
    /// type which it is attached to.
    /// </summary>
    public interface IGrainInterfaceTypeProviderAttribute
    {
        /// <summary>
        /// Gets the grain interface identifier.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="type">The grain interface type.</param>
        /// <returns>
        /// The <see cref="GrainInterfaceType"/> corresponding to the provided type.
        /// </returns>
        GrainInterfaceType GetGrainInterfaceType(IServiceProvider services, Type type);
    }

    /// <summary>
    /// When applied to a grain interface, specifies the <see cref="GrainInterfaceType"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
    public sealed class GrainInterfaceTypeAttribute : Attribute, IGrainInterfaceTypeProviderAttribute
    {
        /// <summary>
        /// The grain interface type.
        /// </summary>
        private readonly GrainInterfaceType _value;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceTypeAttribute"/> instance.
        /// </summary>
        public GrainInterfaceTypeAttribute(string value)
        {
            _value = GrainInterfaceType.Create(value);
        }

        /// <inheritdoc />
        public GrainInterfaceType GetGrainInterfaceType(IServiceProvider services, Type type) => _value;
    }
}
