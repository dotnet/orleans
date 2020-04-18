using System;
using System.Diagnostics;

namespace Orleans.Metadata
{
    /// <summary>
    /// Uniquely identifies a grain interface.
    /// </summary>
    [Serializable]
    [DebuggerDisplay("{Value}")]
    public readonly struct GrainInterfaceId : IEquatable<GrainInterfaceId>
    {
        public readonly string Value;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceId"/> instance.
        /// </summary>
        public GrainInterfaceId(string value) => this.Value = value;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceId"/> instance.
        /// </summary>
        public static GrainInterfaceId Create(string value) => new GrainInterfaceId(value);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is GrainInterfaceId id && this.Equals(id);

        public bool Equals(GrainInterfaceId other) => string.Equals(this.Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(this.Value);

        /// <inheritdoc />
        public override string ToString() => this.Value;
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
    /// Gets a <see cref="GrainInterfaceId"/> from attributes implementing <see cref="IGrainInterfacePropertiesProviderAttribute"/>.
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
        public bool TryGetGrainInterfaceId(Type grainClass, out GrainInterfaceId grainInterfaceId)
        {
            foreach (var attr in grainClass.GetCustomAttributes(inherit: true))
            {
                if (attr is IGrainInterfaceIdProviderAttribute typeProviderAttribute)
                {
                    grainInterfaceId = typeProviderAttribute.GetGrainInterfaceId(this.serviceProvider, grainClass);
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
