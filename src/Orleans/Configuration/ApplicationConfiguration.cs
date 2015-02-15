/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;


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
            get { return defaults.CollectionAgeLimit.HasValue ? defaults.CollectionAgeLimit.Value : GlobalConfiguration.DEFAULT_COLLECTION_AGE_LIMIT; }
        }

        internal TimeSpan ShortestCollectionAgeLimit
        {
            get
            {
                TimeSpan shortest = DefaultCollectionAgeLimit;
                foreach (var typeConfig in ClassSpecific)
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
            classSpecific = new Dictionary<string, GrainTypeConfiguration>();
            defaults = new GrainTypeConfiguration(null)
                {
                    CollectionAgeLimit = defaultCollectionAgeLimit
                };
        }

        /// <summary>
        /// IEnumerable of all configurations for different grain types.
        /// </summary>
        public IEnumerable<GrainTypeConfiguration> ClassSpecific { get { return classSpecific.Values; } }

        /// <summary>
        /// Load this configuratin from xml element.
        /// </summary>
        /// <param name="xmlElement"></param>
        /// <param name="logger"></param>
        public void Load(XmlElement xmlElement, TraceLogger logger)
        {
            bool found = false;
            foreach (XmlNode node in xmlElement.ChildNodes)
            {
                found = true;
                var config = GrainTypeConfiguration.Load((XmlElement)node, logger);
                if (null == config) continue;

                if (config.AreDefaults)
                {
                    defaults = config;
                }
                else
                {
                    if (classSpecific.ContainsKey(config.Type.FullName))
                    {
                        throw new InvalidOperationException(string.Format("duplicate type {0} in configuration", config.Type.FullName));
                    }
                    classSpecific.Add(config.Type.FullName, config);
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
            return GetCollectionAgeLimit(type.FullName);
        }

        /// <summary>
        /// Returns the time period used to collect in-active activations of a given type.
        /// </summary>
        /// <param name="type">Grain type full name.</param>
        /// <returns></returns>
        public TimeSpan GetCollectionAgeLimit(string grainTypeFullName)
        {
            GrainTypeConfiguration config;
            return classSpecific.TryGetValue(grainTypeFullName, out config) && config.CollectionAgeLimit.HasValue ? 
                config.CollectionAgeLimit.Value : DefaultCollectionAgeLimit;
        }


        /// <summary>
        /// Sets the time period  to collect in-active activations for a given type.
        /// </summary>
        /// <param name="type">Grain type full name.</param>
        /// <param name="ageLimit">The age limit to use.</param>
        public void SetCollectionAgeLimit(Type type, TimeSpan ageLimit)
        {
            ThrowIfLessThanZero(ageLimit, "ageLimit");

            GrainTypeConfiguration config;
            if (!classSpecific.TryGetValue(type.FullName, out config))
            {
                config = new GrainTypeConfiguration(type);
                classSpecific[type.FullName] = config;
            }

            config.CollectionAgeLimit = ageLimit;
        }

        /// <summary>
        /// Resets the time period to collect in-active activations for a given type to a default value.
        /// </summary>
        /// <param name="type">Grain type full name.</param>
        /// <param name="ageLimit">The age limit to use.</param>
        public void ResetCollectionAgeLimitToDefault(Type type)
        {
            GrainTypeConfiguration config;
            if (!classSpecific.TryGetValue(type.FullName, out config)) return;

            config.CollectionAgeLimit = null;
        }

        /// <summary>
        /// Sets the default time period  to collect in-active activations for all grain type.
        /// </summary>
        /// <param name="ageLimit">The age limit to use.</param>
        public void SetDefaultCollectionAgeLimit(TimeSpan ageLimit)
        {
            ThrowIfLessThanZero(ageLimit, "ageLimit");
            defaults.CollectionAgeLimit = ageLimit;
        }

        private static void ThrowIfLessThanZero(TimeSpan timeSpan, string paramName)
        {
            if (timeSpan < TimeSpan.Zero) throw new ArgumentOutOfRangeException(paramName);
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
            result.AppendFormat("         Deactivate if idle for: {0}", DefaultCollectionAgeLimit)
                .AppendLine();

            foreach (GrainTypeConfiguration config in classSpecific.Values)
            {
                if (!config.CollectionAgeLimit.HasValue) continue;

                result.AppendFormat("      GrainType Type=\"{0}\":", config.Type.FullName)
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
        public Type         Type { get; private set; }

        /// <summary>
        /// Whether this is a defualt configuration that applies to all grain types.
        /// </summary>
        public bool         AreDefaults { get { return Type == null; } }

        /// <summary>
        /// The time period used to collect in-active activations of this type.
        /// </summary>
        public TimeSpan?    CollectionAgeLimit { get; set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="type">Grain type of this configuration.</param>
        public GrainTypeConfiguration(Type type)
        {
            Type = type;
        }

        /// <summary>
        /// Load this configuratin from xml element.
        /// </summary>
        /// <param name="xmlElement"></param>
        /// <param name="logger"></param>
        public static GrainTypeConfiguration Load(XmlElement xmlElement, TraceLogger logger)
        {
            Type type = null;
            bool areDefaults = xmlElement.LocalName == "Defaults";
            foreach (XmlAttribute attribute in xmlElement.Attributes)
            {
                if (!areDefaults && attribute.LocalName == "Type")
                {
                    string fullName = attribute.Value.Trim();
                    try
                    {
                        type = TypeUtils.ResolveType(fullName);

                    }
                    catch (Exception exception)
                    {
                        logger.Error(ErrorCode.Loader_TypeLoadError, string.Format("Unable to find grain class type specified in configuration ({0}); Ignoring.", fullName), exception);
                        return null;
                    }
                }
                else
                {
                    throw new InvalidOperationException(string.Format("unrecognized attribute {0}", attribute.LocalName));
                }
            }
            if (!areDefaults)
            {
                if (type == null) throw new InvalidOperationException("Type attribute not specified");

                // postcondition: returned type must implement IGrain.
                if (!typeof(IGrain).IsAssignableFrom(type))
                {
                    throw new InvalidOperationException(string.Format("Type {0} must implement IGrain to be used in this context", type.FullName));
                }
                // postcondition: returned type must either be an interface or a class.
                if (!type.IsInterface && !type.IsClass)
                {
                    throw new InvalidOperationException(string.Format("Type {0} must either be an interface or class.", type.FullName));
                }
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

            if (found) return new GrainTypeConfiguration(type) { CollectionAgeLimit = collectionAgeLimit, };
            
            throw new InvalidOperationException(string.Format("empty GrainTypeConfiguration for {0}", type == null ? "defaults" : type.FullName));
        }
    }
}
