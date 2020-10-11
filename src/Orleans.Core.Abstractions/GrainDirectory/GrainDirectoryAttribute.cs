using System;
using System.Collections.Generic;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GrainDirectoryAttribute : Attribute, IGrainPropertiesProviderAttribute
    {
        public const string DEFAULT_GRAIN_DIRECTORY = "default";

        public string GrainDirectoryName { get; set; }

        public GrainDirectoryAttribute()
        {
            this.GrainDirectoryName = DEFAULT_GRAIN_DIRECTORY;
        }

        /// <inheritdoc />
        public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.GrainDirectory] = this.GrainDirectoryName ?? DEFAULT_GRAIN_DIRECTORY;
        }
    }
}
