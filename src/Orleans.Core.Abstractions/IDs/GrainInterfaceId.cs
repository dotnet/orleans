using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Uniquely identifies a grain interface.
    /// </summary>
    [Serializable]
    public readonly struct GrainInterfaceId : IEquatable<GrainInterfaceId>
    {
        private readonly IdSpan _value;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceId"/> instance.
        /// </summary>
        public GrainInterfaceId(string value) => _value = IdSpan.Create(value);

        /// <summary>
        /// Creates a <see cref="GrainInterfaceId"/> instance.
        /// </summary>
        public GrainInterfaceId(IdSpan value) => _value = value;

        /// <summary>
        /// Returns the <see cref="IdSpan"/> value underlying this instance.
        /// </summary>
        public IdSpan Value => _value;

        /// <summary>
        /// Returns true if this value is equal to the <see langword="default"/> instance.
        /// </summary>
        public bool IsDefault => _value.IsDefault;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceId"/> instance.
        /// </summary>
        public static GrainInterfaceId Create(string value) => new GrainInterfaceId(value);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is GrainInterfaceId id && this.Equals(id);

        public bool Equals(GrainInterfaceId other) => _value.Equals(other._value);

        /// <inheritdoc />
        public override int GetHashCode() => _value.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => _value.ToStringUtf8();

        public string ToStringUtf8() => _value.ToString();

        public static bool operator ==(GrainInterfaceId left, GrainInterfaceId right) => left.Equals(right);
        public static bool operator !=(GrainInterfaceId left, GrainInterfaceId right) => !left.Equals(right);
    }

    /// <summary>
    /// Gets a <see cref="GrainInterfaceId"/> for an interface.
    /// </summary>
    public interface IGrainInterfaceIdProvider
    {
        /// <summary>
        /// Gets the <see cref="GrainInterfaceId"/> corresponding to the specified <paramref name="type"/>.
        /// </summary>
        bool TryGetGrainInterfaceId(Type type, out GrainInterfaceId grainInterfaceId);
    }

    /// <summary>
    /// Gets a <see cref="GrainInterfaceId"/> from attributes implementing <see cref="IGrainInterfaceIdProviderAttribute"/>.
    /// </summary>
    public class AttributeGrainInterfaceIdProvider : IGrainInterfaceIdProvider
    {
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Creates a <see cref="AttributeGrainInterfaceIdProvider"/> instance.
        /// </summary>
        public AttributeGrainInterfaceIdProvider(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public bool TryGetGrainInterfaceId(Type type, out GrainInterfaceId grainInterfaceId)
        {
            foreach (var attr in type.GetCustomAttributes(inherit: false))
            {
                if (attr is IGrainInterfaceIdProviderAttribute provider)
                {
                    grainInterfaceId = provider.GetGrainInterfaceId(this.serviceProvider, type);
                    return true;
                }
            }

            grainInterfaceId = default;
            return false;
        }
    }

    /// <summary>
    /// An <see cref="Attribute"/> which implements this specifies the <see cref="GrainInterfaceId"/> of the
    /// type which it is attached to.
    /// </summary>
    public interface IGrainInterfaceIdProviderAttribute
    {
        /// <summary>
        /// Gets the grain interface identifier.
        /// </summary>
        GrainInterfaceId GetGrainInterfaceId(IServiceProvider services, Type type);
    }

    /// <summary>
    /// Specifies the <see cref="GrainInterfaceId"/> of the type which it is attached to.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
    public sealed class GrainInterfaceIdAttribute : Attribute, IGrainInterfaceIdProviderAttribute
    {
        private readonly GrainInterfaceId value;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceIdAttribute"/> instance.
        /// </summary>
        public GrainInterfaceIdAttribute(string value)
        {
            this.value = GrainInterfaceId.Create(value);
        }

        /// <inheritdoc />
        public GrainInterfaceId GetGrainInterfaceId(IServiceProvider services, Type type) => this.value;
    }
}
