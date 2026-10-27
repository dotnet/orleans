using System;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;

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
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="AttributeGrainTypeProvider"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The service provider.
        /// </param>
        public AttributeGrainTypeProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public bool TryGetGrainType(Type grainClass, out GrainType grainType)
        {
            foreach (var attr in grainClass.GetCustomAttributes(inherit: false))
            {
                if (attr is IGrainTypeProviderAttribute typeProviderAttribute)
                {
                    grainType = typeProviderAttribute.GetGrainType(this._serviceProvider, grainClass);
                    return true;
                }
            }

            grainType = default;
            return false;
        }
    }

    /// <summary>
    /// Functionality which can be implemented by a custom <see cref="Attribute"/> which implements this specifies the <see cref="GrainType"/> of the
    /// type which it is attached to.
    /// </summary>
    public interface IGrainTypeProviderAttribute
    {
        /// <summary>
        /// Gets the <see cref="GrainType"/> for the attached <see cref="Type"/>.
        /// </summary>
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <param name="type">
        /// The grain class.
        /// </param>
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
        /// <summary>
        /// The grain type name.
        /// </summary>
        private readonly GrainType _grainType;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainTypeAttribute"/> class.
        /// </summary>
        /// <param name="grainType">
        /// A non-empty grain type name containing no quotes, whitespace, control characters, or reserved type-name delimiters (<c>[](),*&amp;</c>).
        /// </param>
        /// <remarks>Generic arity suffixes must be decimal integers at the end of each nested type name, totaling at most 64.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="grainType"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="grainType"/> is empty, contains a quote, whitespace, a control character, or a reserved type-name delimiter, or has an invalid generic arity suffix.</exception>
        public GrainTypeAttribute(string grainType)
        {
            IdentityNameValidator.Validate(grainType, nameof(grainType));
            this._grainType = GrainType.Create(grainType);
        }

        /// <inheritdoc />
        public GrainType GetGrainType(IServiceProvider services, Type type) => this._grainType;
    }
}
