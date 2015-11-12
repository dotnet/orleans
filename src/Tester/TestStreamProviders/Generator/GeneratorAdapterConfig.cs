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

using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Providers;

namespace Tester.TestStreamProviders.Generator
{
    /// <summary>
    /// This configuration class is used to configure the GeneratorStreamProvider.
    /// It tells the stream provider how many queues to create, and which generator to use to generate event streams.
    /// </summary>
    public class GeneratorAdapterConfig
    {
        public const string GeneratorConfigTypeName = "GeneratorConfigType";
        public Type GeneratorConfigType { get; set; }

        public string StreamProviderName { get; private set; }

        public int CacheSize { get { return 1024; } }

        private const string TotalQueueCountName = "TotalQueueCount";
        private const int TotalQueueCountDefault = 4;
        public int TotalQueueCount { get; set; }

        public GeneratorAdapterConfig(string streamProviderName)
        {
            StreamProviderName = streamProviderName;
            TotalQueueCount = TotalQueueCountDefault;
        }

        /// <summary>
        /// Utility function to convert config to property bag for use in stream provider configuration
        /// </summary>
        /// <returns></returns>
        public void WriteProperties(Dictionary<string, string> properties)
        {
            properties.Add(GeneratorConfigTypeName, GeneratorConfigType.AssemblyQualifiedName);
            properties.Add(TotalQueueCountName, TotalQueueCount.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public virtual void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            GeneratorConfigType = providerConfiguration.GetTypeProperty(GeneratorConfigTypeName, null);
            if (GeneratorConfigType == null)
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", "GeneratorConfigType not set.");
            }
            if (string.IsNullOrWhiteSpace(StreamProviderName))
            {
                throw new ArgumentOutOfRangeException("providerConfiguration", "StreamProviderName not set.");
            }
            TotalQueueCount = providerConfiguration.GetIntProperty(TotalQueueCountName, TotalQueueCountDefault);
        }
    }
}
