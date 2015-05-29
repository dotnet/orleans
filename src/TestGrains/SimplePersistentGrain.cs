﻿/*
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
    public class SimplePersistentGrain_State : IGrainState
    {
        public int IntProperty { get; set; }
        public string StringProperty { get; set; }
        public string Etag { get; set; }
        public Task ClearStateAsync()
        {
            throw new NotImplementedException();
        }

        public Task WriteStateAsync()
        {
            throw new NotImplementedException();
        }

        public Task ReadStateAsync()
        {
            throw new NotImplementedException();
        }

        public IDictionary<string, object> AsDictionary()
        {
            throw new NotImplementedException();
        }

        public void SetAll(IDictionary<string, object> values)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class SimplePersistentGrain : Grain<SimplePersistentGrain_State>, ISimpleGrain
    {
        public Task SetA(int a)
        {
            throw new NotImplementedException();
        }

        public Task SetB(int a)
        {
            throw new NotImplementedException();
        }

        public Task IncrementA()
        {
            throw new NotImplementedException();
        }

        public Task<int> GetAxB()
        {
            throw new NotImplementedException();
        }

        public Task<int> GetAxB(int a, int b)
        {
            throw new NotImplementedException();
        }

        public Task<int> GetA()
        {
            throw new NotImplementedException();
        }
    }
}