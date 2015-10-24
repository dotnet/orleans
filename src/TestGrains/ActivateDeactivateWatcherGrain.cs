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
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ActivateDeactivateWatcherGrain : Grain, IActivateDeactivateWatcherGrain
    {
        private Logger logger;

        private readonly List<string> activationCalls = new List<string>();
        private readonly List<string> deactivationCalls = new List<string>();

        public Task<string[]> GetActivateCalls() { return Task.FromResult(activationCalls.ToArray()); }
        public Task<string[]> GetDeactivateCalls() { return Task.FromResult(deactivationCalls.ToArray()); }

        public override Task OnActivateAsync()
        {
            this.logger = GetLogger();
            return base.OnActivateAsync();
        }

        public Task Clear()
        {
            if (logger.IsVerbose) logger.Verbose("Clear");
            activationCalls.Clear();
            deactivationCalls.Clear();
            return TaskDone.Done;
        }
        public Task RecordActivateCall(string activation)
        {
            if (logger.IsVerbose) logger.Verbose("RecordActivateCall: " + activation);
            activationCalls.Add(activation);
            return TaskDone.Done;
        }

        public Task RecordDeactivateCall(string activation)
        {
            if (logger.IsVerbose) logger.Verbose("RecordDeactivateCall: " + activation);
            deactivationCalls.Add(activation);
            return TaskDone.Done;
        }
    }
}
