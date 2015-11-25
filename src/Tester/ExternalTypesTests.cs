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
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using System.Collections.Generic;
using Orleans.Serialization;
using TesterExternalModels;
using TestGrainInterfaces;

namespace UnitTests.General
{
    /// <summary>
    /// Unit tests for grains implementing generic interfaces
    /// </summary>
    [TestClass]
    public class ExternalTypesTests : UnitTestSiloHost
    {
        public ExternalTypesTests()
            : base(new TestingSiloOptions { StartPrimary = true, StartSecondary = false })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }
        
        [Ignore, TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithAbstractExternalTypeParam()
        {
            // This solves the problem:
            //SerializationManager.Register(typeof(AbstractModel));

            var grainWitAbstractTypeParam = GrainClient.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWitAbstractTypeParam.GetAbstractModel(new List<AbstractModel>() {new ConcreteModel()});
        }

        [Ignore, TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithEnumxternalTypeParam()
        {
            // This solves the problem
            //SerializationManager.Register(typeof(MyEnum));

            var grainWithEnumTypeParam = GrainClient.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWithEnumTypeParam.GetEnumModel();
        }
    }
}
