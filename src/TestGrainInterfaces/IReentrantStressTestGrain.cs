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
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace TestInternalGrainInterfaces
{
    public interface IReentrantStressTestGrain : IGrainWithIntegerKey
    {
        Task<byte[]> Echo(byte[] data);

        Task<string> GetRuntimeInstanceId();

        Task Ping(byte[] data);

        Task PingWithDelay(byte[] data, TimeSpan delay);

        Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote);

        Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote);

        Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote);

        Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain, bool nextGrainIsRemote);

        Task InterleavingConsistencyTest(int numItems);
    }

    public interface IReentrantLocalStressTestGrain : IGrainWithIntegerKey
    {
        Task<byte[]> Echo(byte[] data);

        Task<string> GetRuntimeInstanceId();

        Task Ping(byte[] data);

        Task PingWithDelay(byte[] data, TimeSpan delay);

        Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote);

        Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote);

        Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote);

        Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain, bool nextGrainIsRemote);
    }
}
