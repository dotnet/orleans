using System;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    /// <summary>
    /// Uniquely identifies a grain interface.
    /// </summary>
    [Serializable, Immutable]
    public readonly struct GrainInterfaceType : IEquatable<GrainInterfaceType>
    {
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

        public bool Equals(GrainInterfaceType other) => _value.Equals(other._value);

        /// <inheritdoc />
        public override int GetHashCode() => _value.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => _value.ToStringUtf8();

        public string ToStringUtf8() => _value.ToStringUtf8();

        public static bool operator ==(GrainInterfaceType left, GrainInterfaceType right) => left.Equals(right);
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
        bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType grainInterfaceType);
    }

    /// <summary>
    /// Gets a <see cref="GrainInterfaceType"/> from attributes implementing <see cref="IGrainInterfaceTypeProviderAttribute"/>.
    /// </summary>
    public class AttributeGrainInterfaceTypeProvider : IGrainInterfaceTypeProvider
    {
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Creates a <see cref="AttributeGrainInterfaceTypeProvider"/> instance.
        /// </summary>
        public AttributeGrainInterfaceTypeProvider(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public bool TryGetGrainInterfaceType(Type type, out GrainInterfaceType grainInterfaceType)
        {
            foreach (var attr in type.GetCustomAttributes(inherit: false))
            {
                if (attr is IGrainInterfaceTypeProviderAttribute provider)
                {
                    grainInterfaceType = provider.GetGrainInterfaceType(this.serviceProvider, type);
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
        GrainInterfaceType GetGrainInterfaceType(IServiceProvider services, Type type);
    }

    /// <summary>
    /// Specifies the <see cref="GrainInterfaceType"/> of the type which it is attached to.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
    public sealed class GrainInterfaceTypeAttribute : Attribute, IGrainInterfaceTypeProviderAttribute
    {
        private readonly GrainInterfaceType value;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceTypeAttribute"/> instance.
        /// </summary>
        public GrainInterfaceTypeAttribute(string value)
        {
            this.value = GrainInterfaceType.Create(value);
        }

        /// <inheritdoc />
        public GrainInterfaceType GetGrainInterfaceType(IServiceProvider services, Type type) => this.value;
    }
}
