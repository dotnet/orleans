using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Metadata;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// The VersionAttribute allows to specify the version number of the interface
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class VersionAttribute : Attribute, IGrainInterfacePropertiesProviderAttribute
    {
        public ushort Version { get; private set; }

        public VersionAttribute(ushort version)
        {
            Version = version;
        }

        /// <inheritdoc />
        void IGrainInterfacePropertiesProviderAttribute.Populate(IServiceProvider services, Type type, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainInterfaceProperties.Version] = this.Version.ToString(CultureInfo.InvariantCulture);
        }
    }
}
