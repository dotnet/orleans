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
using System.Threading.Tasks;
using HelloGeoInterfaces;
using Orleans.Runtime;
using Microsoft.Azure;
using Orleans.MultiCluster;

namespace HelloGeoGrains
{
    /// <summary>
    /// Implementation of the Hello grain (same for both versions)
    ///  </summary>
    public class HelloGrain : Orleans.Grain, IHelloGrain
    {

        int count = 0; // counts the number of pings

        Task<string> IHelloGrain.Ping()
        {
            var answer = string.Format("Hello #{0}\n(on machine \"{2}\" in cluster \"{1}\")",
                ++count, CloudConfigurationManager.GetSetting("ClusterId"), Environment.MachineName);

            return Task.FromResult(answer);
        }
    }

    /// <summary>
    /// One-per-cluster version
    /// </summary>
    [OneInstancePerCluster]
    public class OneInstancePerClusterGrain : HelloGrain {}
 

    /// <summary>
    /// Global-single-instance version
    /// </summary>
    [GlobalSingleInstance]
    public class GlobalSingleInstanceGrain : HelloGrain {}
 

}
