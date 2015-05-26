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
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    class SimpleGenericGrain<T> :Grain, ISimpleGenericGrain<T>
    {
        protected T Value { get; set; }

        public virtual Task Set(T t)
        {
            Value = t;
            return TaskDone.Done;
        }

        public virtual Task Transform()
        {
            return TaskDone.Done;
        }

        public Task<T> Get()
        {
            return Task.FromResult(Value);
        }

        public Task CompareGrainReferences()
        {
            long pk = this.GetPrimaryKeyLong() + 1;

            var gr1 = GrainFactory.GetGrain<ISimpleGenericGrain<float>>(pk);
            var gr2 = SimpleGenericGrainFactory<float>.GetGrain(pk);

            // This equality currently work, since the SimpleGenericGrain<float> was never created before, and as a result both grs are the same.
            if (!gr1.Equals(gr2))
            {
                throw new Exception(String.Format("Case_1: 2 grain references are different, while should have been the same: gr1={0}, gr2={1}", gr1, gr2));
            }

            var gr3 = GrainFactory.GetGrain<ISimpleGenericGrain<T>>(pk);
            var gr4 = SimpleGenericGrainFactory<string>.GetGrain(pk);

            if (typeof(T).Equals(typeof(string)))
            {
                // This equality currently fails. 
                // SimpleGenericGrain<string> was already instantiated once before (this grain), and as a result grs are the different for some reason.
                if (!gr3.Equals(gr4))
                {
                    throw new Exception(String.Format("Case_2: 2 grain references are different, while should have been the same: gr1={0}, gr2={1}", gr3, gr4));
                }
            }
            return TaskDone.Done;
        }
    }
}
