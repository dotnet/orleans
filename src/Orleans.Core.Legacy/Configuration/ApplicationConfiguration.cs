using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Orleans application configuration parameters.
    /// </summary>
    [Serializable]
    public class ApplicationConfiguration
    {
        private readonly Dictionary<string, GrainTypeConfiguration> classSpecific;
        private GrainTypeConfiguration defaults;

        /// <summary>
        /// The default time period used to collect in-active activations.
        /// Applies to all grain types.
        /// </summary>
        public TimeSpan DefaultCollectionAgeLimit
        {
            get { return this.defaults.CollectionAgeLimit ?? GrainCollectionOptions.DEFAULT_COLLECTION_AGE_LIMIT; }
        }

        internal TimeSpan ShortestCollectionAgeLimit
        {
            get
            {
                TimeSpan shortest = this.DefaultCollectionAgeLimit;
                foreach (var typeConfig in this.ClassSpecific)
                {
                    TimeSpan curr = typeConfig.CollectionAgeLimit.Value;
                    if (curr < shortest)
                    {
                        shortest = curr;
                    }
                }
                return shortest;
            }
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="defaultCollectionAgeLimit">The default time period used to collect in-active activations.</param>
        public ApplicationConfiguration(TimeSpan? defaultCollectionAgeLimit = null)
        {
            this.classSpecific = new Dictionary<string, GrainTypeConfiguration>();
            this.defaults = new GrainTypeConfiguration(null, defaultCollectionAgeLimit);
        }

        /// <summary>
        /// IEnumerable of all configurations for different grain types.
        /// </summary>
        public IEnumerable<GrainTypeConfiguration> ClassSpecific { get { return this.classSpecific.Values; } }

        /// <summary>
        /// Load this configuratin from xml element.
        /// </summary>
        /// <param name="xmlElement"></param>
        public void Load(XmlElement xmlElement)
        {
            bool found = false;
            foreach (XmlNode node in xmlElement.ChildNodes)
            {
                found = true;
                var config = GrainTypeConfiguration.Load((XmlElement)node);
                if (null == config) continue;

                if (config.AreDefaults)
                {
                    this.defaults = config;
                }
                else
                {
                    if (this.classSpecific.ContainsKey(config.FullTypeName))
                    {
                        throw new InvalidOperationException(string.Format("duplicate type {0} in configuration", config.FullTypeName));
                    }
                    this.classSpecific.Add(config.FullTypeName, config);
                }
            }

            if (!found)
            {
                throw new InvalidOperationException("empty GrainTypeConfiguration element");
            }
        }

        /// <summary>
        /// Returns the time period used to collect in-active activations of a given type.
        /// </summary>
        /// <param name="type">Grain type.</param>
        /// <returns></returns>
        public TimeSpan GetCollectionAgeLimit(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }
            return GetCollectionAgeLimit(type.FullName);
        }

        /// <summary>
        /// Returns the time period used to collect in-active activations of a given type.
        /// </summary>
        /// <param name="grainTypeFullName">Grain type full name.</param>
        /// <returns></returns>
        public TimeSpan GetCollectionAgeLimit(string grainTypeFullName)
        {
            if (string.IsNullOrEmpty(grainTypeFullName))
            {
                throw new ArgumentNullException("grainTypeFullName");
            }
            GrainTypeConfiguration config;
            return this.classSpecific.TryGetValue(grainTypeFullName, out config) && config.CollectionAgeLimit.HasValue ? 
                config.CollectionAgeLimit.Value : this.DefaultCollectionAgeLimit;
        }


        /// <summary>
        /// Sets the time period  to collect in-active activations for a given type.
        /// </summary>
        /// <param name="type">Grain type full name.</param>
        /// <param name="ageLimit">The age limit to use.</param>
        public void SetCollectionAgeLimit(Type type, TimeSpan ageLimit)
        {
            if (type == null)
            {
                throw new ArgumentNullException("type");
            }
            SetCollectionAgeLimit(type.FullName, ageLimit);
        }

        /// <summary>
        /// Sets the time period  to collect in-active activations for a given type.
        /// </summary>
        /// <param name="grainTypeFullName">Grain type full name string.</param>
        /// <param name="ageLimit">The age limit to use.</param>
        public void SetCollectionAgeLimit(string grainTypeFullName, TimeSpan ageLimit)
        {
            if (string.IsNullOrEmpty(grainTypeFullName))
            {
                throw new ArgumentNullException("grainTypeFullName");
            }
            ThrowIfLessThanZero(ageLimit, "ageLimit");

            GrainTypeConfiguration config;
            if (!this.classSpecific.TryGetValue(grainTypeFullName, out config))
            {
                config = new GrainTypeConfiguration(grainTypeFullName);
                this.classSpecific[grainTypeFullName] = config;
            }

            config.SetCollectionAgeLimit(ageLimit);
        }

        /// <summary>
        /// Resets the time period to collect in-active activations for a given type to a default value.
        /// </summary>
        /// <param name="type">Grain type full name.</param>
        public void ResetCollectionAgeLimitToDefault(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            ResetCollectionAgeLimitToDefault(type.FullName);
        }

        /// <summary>
        /// Resets the time period to collect in-active activations for a given type to a default value.
        /// </summary>
        /// <param name="grainTypeFullName">Grain type full name.</param>
        public void ResetCollectionAgeLimitToDefault(string grainTypeFullName)
        {
            if (string.IsNullOrEmpty(grainTypeFullName))
            {
                throw new ArgumentNullException(nameof(grainTypeFullName));
            }
            GrainTypeConfiguration config;
            if (!this.classSpecific.TryGetValue(grainTypeFullName, out config)) return;

            config.SetCollectionAgeLimit(null);
        }

        /// <summary>
        /// Sets the default time period  to collect in-active activations for all grain type.
        /// </summary>
        /// <param name="ageLimit">The age limit to use.</param>
        public void SetDefaultCollectionAgeLimit(TimeSpan ageLimit)
        {
            ThrowIfLessThanZero(ageLimit, "ageLimit");
            this.defaults.SetCollectionAgeLimit(ageLimit);
        }

        private static void ThrowIfLessThanZero(TimeSpan timeSpan, string paramName)
        {
            if (timeSpan < TimeSpan.Zero) throw new ArgumentOutOfRangeException(paramName);
        }

        internal void ValidateConfiguration(ILogger logger)
        {
            foreach (GrainTypeConfiguration config in this.classSpecific.Values)
            {
                config.ValidateConfiguration(logger);
            }
        }

        /// <summary>
        /// Prints the current application configuration.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            var result = new StringBuilder();
            result.AppendFormat("   Application:").AppendLine();
            result.AppendFormat("      Defaults:").AppendLine();
            result.AppendFormat("         Deactivate if idle for: {0}", this.DefaultCollectionAgeLimit)
                .AppendLine();

            foreach (GrainTypeConfiguration config in this.classSpecific.Values)
            {
                if (!config.CollectionAgeLimit.HasValue) continue;

                result.AppendFormat("      GrainType Type=\"{0}\":", config.FullTypeName)
                    .AppendLine();
                result.AppendFormat("         Deactivate if idle for: {0} seconds", (long)config.CollectionAgeLimit.Value.TotalSeconds)
                    .AppendLine();
            }
            return result.ToString();
        }
    }

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
        public bool AreDefaults { get { return this.FullTypeName == null; } }

        /// <summary>
        /// The time period used to collect in-active activations of this type.
        /// </summary>
        public TimeSpan? CollectionAgeLimit { get { return this.collectionAgeLimit; } }

        private TimeSpan? collectionAgeLimit;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="type">Grain type of this configuration.</param>
        public GrainTypeConfiguration(string type)
        {
            this.FullTypeName = type;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="type">Grain type of this configuration.</param>
        /// <param name="ageLimit">Age limit for this type.</param>
        public GrainTypeConfiguration(string type, TimeSpan? ageLimit)
        {
            this.FullTypeName = type;
            SetCollectionAgeLimit(ageLimit);
        }

        /// <summary>Sets a custom collection age limit for a grain type.</summary>
        /// <param name="ageLimit">Age limit for this type.</param>
        public void SetCollectionAgeLimit(TimeSpan? ageLimit)
        {
            if (ageLimit == null)
            {
                this.collectionAgeLimit = null;
            }

            TimeSpan minAgeLimit = GrainCollectionOptions.DEFAULT_COLLECTION_QUANTUM;
            if (ageLimit < minAgeLimit)
            {
                if (GlobalConfiguration.ENFORCE_MINIMUM_REQUIREMENT_FOR_AGE_LIMIT)
                {
                    throw new ArgumentOutOfRangeException($"The AgeLimit attribute is required to be at least {minAgeLimit}.");
                }
            }
            this.collectionAgeLimit = ageLimit;
        }

        /// <summary>
        /// Load this configuration from xml element.
        /// </summary>
        /// <param name="xmlElement"></param>
        public static GrainTypeConfiguration Load(XmlElement xmlElement)
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

            throw new InvalidOperationException(string.Format("empty GrainTypeConfiguration for {0}", fullTypeName ?? "defaults"));
        }

        internal void ValidateConfiguration(ILogger logger)
        {
            if (this.AreDefaults) return;

            Type type = null;               
            try
            {
                type = new CachedTypeResolver().ResolveType(this.FullTypeName);
            }
            catch (Exception exception)
            {
                string errStr = string.Format("Unable to find grain class type {0} specified in configuration; Failing silo startup.", this.FullTypeName);
                logger.Error(ErrorCode.Loader_TypeLoadError, errStr, exception);
                throw new OrleansException(errStr, exception);
            }

            if (type == null)
            {
                string errStr = string.Format("Unable to find grain class type {0} specified in configuration; Failing silo startup.", this.FullTypeName);
                logger.Error(ErrorCode.Loader_TypeLoadError_2, errStr);
                throw new OrleansException(errStr);
            }
            var typeInfo = type.GetTypeInfo();
            // postcondition: returned type must implement IGrain.
            if (!typeof(IGrain).IsAssignableFrom(type))
            {
                string errStr = string.Format("Type {0} must implement IGrain to be used Application configuration context.",type.FullName);
                logger.Error(ErrorCode.Loader_TypeLoadError_3, errStr);
                throw new OrleansException(errStr);
            }
            // postcondition: returned type must either be an interface or a class.
            
            if (!typeInfo.IsInterface && !typeInfo.IsClass)
            {
                string errStr = string.Format("Type {0} must either be an interface or class used Application configuration context.",type.FullName);
                logger.Error(ErrorCode.Loader_TypeLoadError_4, errStr);
                throw new OrleansException(errStr);
            }
        }
    }
}
