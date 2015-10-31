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
using Orleans.Providers;
using Orleans.Streams;

namespace Tester.TestStreamProviders.Generator
{
    /// <summary>
    /// Interface of generators used by the GeneratorStreamProvider.  Any method of generating events
    ///  must conform to this interface to be used by the GeneratorStreamProvider.
    /// </summary>
    public interface IStreamGenerator
    {
        bool TryReadEvents(DateTime utcNow, out List<IBatchContainer> events);
        void Configure(IServiceProvider serviceProvider, IStreamGeneratorConfig generatorConfig);
    }

    /// <summary>
    /// Interface of configuration for generators used by the GeneratorStreamProvider.  This interface covers
    ///   the minimal set of information the stream provider needs to configure a generator to generate data.  Generators should
    ///   add any additional configuration information needed to it's implementation of this interface.
    /// </summary>
    public interface IStreamGeneratorConfig
    {
        Type StreamGeneratorType { get; }
        void PopulateFromProviderConfig(IProviderConfiguration providerConfiguration);
    }
}
