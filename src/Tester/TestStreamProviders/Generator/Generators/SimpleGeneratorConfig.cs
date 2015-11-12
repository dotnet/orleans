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

namespace Tester.TestStreamProviders.Generator.Generators
{
    /// <summary>
    /// Simple generator configuration class.
    /// This class is used to configure a generator stream provider to generate streams using the SimpleGenerator
    /// </summary>
    [Serializable]
    public class SimpleGeneratorConfig : IStreamGeneratorConfig
    {
        private const string StreamNamespaceName = "StreamNamespace";
        public string StreamNamespace { get; set; }

        public Type StreamGeneratorType { get { return typeof (SimpleGenerator); } }
        
        /// <summary>
        /// Nuber of events to generate on this stream
        /// </summary>
        public int EventsInStream { get; set; }
        private const string EventsInStreamName = "EventsInStream";
        private const int EventsInStreamDefault = 100;

        public SimpleGeneratorConfig()
        {
            EventsInStream = EventsInStreamDefault;
        }

        /// <summary>
        /// Utility function to convert config to property bag for use in stream provider configuration
        /// </summary>
        /// <returns></returns>
        public void WriteProperties(Dictionary<string, string> properties)
        {
            properties.Add(GeneratorAdapterFactory.GeneratorConfigTypeName, GetType().AssemblyQualifiedName);
            properties.Add(EventsInStreamName, EventsInStream.ToString(CultureInfo.InvariantCulture));
            properties.Add(StreamNamespaceName, StreamNamespace);
        }

        /// <summary>
        /// Utility function to populate config from provider config
        /// </summary>
        /// <param name="providerConfiguration"></param>
        public void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration)
        {
            EventsInStream = providerConfiguration.GetIntProperty(EventsInStreamName, EventsInStreamDefault);
            StreamNamespace = providerConfiguration.GetProperty(StreamNamespaceName, null);
        }
    }
}
