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
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class SimpleRequestContextGrain : Grain, ISimpleRequestContextGrain
    {
        protected Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger(String.Format("{0}-{1}-{2}", typeof(SimpleGrain).Name, base.IdentityString, base.RuntimeIdentity));
            logger.Info("Activate.");
            return TaskDone.Done;
        }

        public Task GetRequestContext()
        {
            RequestContext.Set("GrainInfo", 10L);
            return TaskDone.Done;
        }

        public async Task<object> ReadRequestContext()
        {
            var info = RequestContext.Get("GrainInfo");
            return info;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync.");
            return TaskDone.Done;
        }
    }
}
