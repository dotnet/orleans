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
        /// <summary>
        /// Initializes a new instance of the <see cref="VersionAttribute"/> class.
        /// </summary>
        /// <param name="version">
        /// The version.
        /// </param>
        public VersionAttribute(ushort version)
        {
            Version = version;
        }

        /// <summary>
        /// Gets the version.
        /// </summary>
        public ushort Version { get; private set; }

        /// <inheritdoc />
        void IGrainInterfacePropertiesProviderAttribute.Populate(IServiceProvider services, Type type, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainInterfaceProperties.Version] = this.Version.ToString(CultureInfo.InvariantCulture);
        }
    }
}
