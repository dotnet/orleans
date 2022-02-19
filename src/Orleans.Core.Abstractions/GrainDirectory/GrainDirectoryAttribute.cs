using System;
using System.Collections.Generic;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Specifies the name of the grain directory provider to use for the grain class which this attribute is applied to.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GrainDirectoryAttribute : Attribute, IGrainPropertiesProviderAttribute
    {
        /// <summary>
        /// The default grain directory.
        /// </summary>
        public const string DEFAULT_GRAIN_DIRECTORY = "default";

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainDirectoryAttribute"/> class.
        /// </summary>
        public GrainDirectoryAttribute() : this(DEFAULT_GRAIN_DIRECTORY)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainDirectoryAttribute"/> class.
        /// </summary>
        /// <param name="grainDirectoryName">
        /// The grain directory provider name.
        /// </param>
        public GrainDirectoryAttribute(string grainDirectoryName)
        {
            this.GrainDirectoryName = grainDirectoryName;
        }

        /// <summary>
        /// Gets or sets the grain directory provider name.
        /// </summary>
        public string GrainDirectoryName { get; set; }

        /// <inheritdoc />
        public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.GrainDirectory] = this.GrainDirectoryName ?? DEFAULT_GRAIN_DIRECTORY;
        }
    }
}
