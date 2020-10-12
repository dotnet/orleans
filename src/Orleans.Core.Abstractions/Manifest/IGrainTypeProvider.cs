using System;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Associates a <see cref="GrainType"/> with a grain class.
    /// </summary>
    public interface IGrainTypeProvider
    {
        /// <summary>
        /// Returns the grain type corresponding to the class identified by <paramref name="type"/>.
        /// </summary>
        bool TryGetGrainType(Type type, out GrainType grainType);
    }

    /// <summary>
    /// Gets the corresponding <see cref="GrainType"/> for a grain class from an <see cref="Attribute"/>
    /// implementing <see cref="IGrainTypeProviderAttribute"/> on that class.
    /// </summary>
    public class AttributeGrainTypeProvider : IGrainTypeProvider
    {
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Creates a <see cref="AttributeGrainTypeProvider"/> instance.
        /// </summary>
        public AttributeGrainTypeProvider(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public bool TryGetGrainType(Type grainClass, out GrainType grainType)
        {
            foreach (var attr in grainClass.GetCustomAttributes(inherit: false))
            {
                if (attr is IGrainTypeProviderAttribute typeProviderAttribute)
                {
                    grainType = typeProviderAttribute.GetGrainType(this.serviceProvider, grainClass);
                    return true;
                }
            }

            grainType = default;
            return false;
        }
    }

    /// <summary>
    /// An <see cref="Attribute"/> which implements this specifies the <see cref="GrainType"/> of the
    /// type which it is attached to.
    /// </summary>
    public interface IGrainTypeProviderAttribute
    {
        /// <summary>
        /// Gets the <see cref="GrainType"/> for the attached <see cref="Type"/>.
        /// </summary>
        GrainType GetGrainType(IServiceProvider services, Type type);
    }
}

namespace Orleans
{
    /// <summary>
    /// Specifies the grain type of the grain class which it is attached to.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class GrainTypeAttribute : Attribute, IGrainTypeProviderAttribute
    {
        private readonly GrainType grainType;

        /// <summary>
        /// Creates a <see cref="GrainTypeAttribute"/> instance.
        /// </summary>
        public GrainTypeAttribute(string grainType)
        {
            this.grainType = GrainType.Create(grainType);
        }

        /// <inheritdoc />
        public GrainType GetGrainType(IServiceProvider services, Type type) => this.grainType;
    }
}
