using System;
using System.Reflection;
using System.Xml;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Grain type specific application configuration.
    /// </summary>
    [Serializable]
    public class GrainTypeConfiguration
    {
        /// <summary>
        /// The type of the grain of this configuration.
        /// </summary>
        public string FullTypeName { get; private set; }

        /// <summary>
        /// Whether this is a defualt configuration that applies to all grain types.
        /// </summary>
        public bool AreDefaults { get { return FullTypeName == null; } }

        /// <summary>
        /// The time period used to collect in-active activations of this type.
        /// </summary>
        public TimeSpan? CollectionAgeLimit { get { return collectionAgeLimit; } }

        private TimeSpan? collectionAgeLimit;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="type">Grain type of this configuration.</param>
        public GrainTypeConfiguration(string type)
        {
            FullTypeName = type;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="type">Grain type of this configuration.</param>
        /// <param name="ageLimit">Age limit for this type.</param>
        public GrainTypeConfiguration(string type, TimeSpan? ageLimit)
        {
            FullTypeName = type;
            SetCollectionAgeLimit(ageLimit);
        }

        /// <summary>Sets a custom collection age limit for a grain type.</summary>
        /// <param name="ageLimit">Age limit for this type.</param>
        public void SetCollectionAgeLimit(TimeSpan? ageLimit)
        {
            if (ageLimit == null)
            {
                collectionAgeLimit = null;
            }

            TimeSpan minAgeLimit = GlobalConfiguration.DEFAULT_COLLECTION_QUANTUM;
            if (ageLimit < minAgeLimit)
            {
                if (GlobalConfiguration.ENFORCE_MINIMUM_REQUIREMENT_FOR_AGE_LIMIT)
                {
                    throw new ArgumentOutOfRangeException($"The AgeLimit attribute is required to be at least {minAgeLimit}.");
                }
            }
            collectionAgeLimit = ageLimit;
        }

        /// <summary>
        /// Load this configuration from xml element.
        /// </summary>
        /// <param name="xmlElement"></param>
        /// <param name="logger"></param>
        public static GrainTypeConfiguration Load(XmlElement xmlElement, Logger logger)
        {
            string fullTypeName = null;
            bool areDefaults = xmlElement.LocalName == "Defaults";
            foreach (XmlAttribute attribute in xmlElement.Attributes)
            {
                if (!areDefaults && attribute.LocalName == "Type")
                {
                    fullTypeName = attribute.Value.Trim();
                }
                else
                {
                    throw new InvalidOperationException(string.Format("unrecognized attribute {0}", attribute.LocalName));
                }
            }
            if (!areDefaults)
            {
                if (fullTypeName == null) throw new InvalidOperationException("Type attribute not specified");
            }
            bool found = false;
            TimeSpan? collectionAgeLimit = null;
            foreach (XmlNode node in xmlElement.ChildNodes)
            {
                var child = (XmlElement)node;
                switch (child.LocalName)
                {
                    default:
                        throw new InvalidOperationException(string.Format("unrecognized XML element {0}", child.LocalName));
                    case "Deactivation":
                        found = true;
                        collectionAgeLimit = ConfigUtilities.ParseCollectionAgeLimit(child);
                        break;
                }
            }

            if (found) return new GrainTypeConfiguration(fullTypeName, collectionAgeLimit);

            throw new InvalidOperationException(string.Format("empty GrainTypeConfiguration for {0}", fullTypeName == null ? "defaults" : fullTypeName));
        }

        internal void ValidateConfiguration(Logger logger)
        {
            if (AreDefaults) return;

            Type type = null;               
            try
            {
                type = TypeUtils.ResolveType(FullTypeName);
            }
            catch (Exception exception)
            {
                string errStr = String.Format("Unable to find grain class type {0} specified in configuration; Failing silo startup.", FullTypeName);
                logger.Error(ErrorCode.Loader_TypeLoadError, errStr, exception);
                throw new OrleansException(errStr, exception);
            }

            if (type == null)
            {
                string errStr = String.Format("Unable to find grain class type {0} specified in configuration; Failing silo startup.", FullTypeName);
                logger.Error(ErrorCode.Loader_TypeLoadError_2, errStr);
                throw new OrleansException(errStr);
            }
            var typeInfo = type.GetTypeInfo();
            // postcondition: returned type must implement IGrain.
            if (!typeof(IGrain).IsAssignableFrom(type))
            {
                string errStr = String.Format("Type {0} must implement IGrain to be used Application configuration context.",type.FullName);
                logger.Error(ErrorCode.Loader_TypeLoadError_3, errStr);
                throw new OrleansException(errStr);
            }
            // postcondition: returned type must either be an interface or a class.
            
            if (!typeInfo.IsInterface && !typeInfo.IsClass)
            {
                string errStr = String.Format("Type {0} must either be an interface or class used Application configuration context.",type.FullName);
                logger.Error(ErrorCode.Loader_TypeLoadError_4, errStr);
                throw new OrleansException(errStr);
            }
        }
    }
}