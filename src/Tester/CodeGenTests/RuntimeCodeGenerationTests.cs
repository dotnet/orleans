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

namespace Tester.CodeGenTests
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using Microsoft.VisualStudio.TestTools.UnitTesting;
    
    using Orleans.Serialization;

    using UnitTests.Tester;

    /// <summary>
    /// Tests runtime code generation.
    /// </summary>
    [TestClass]
    public class RuntimeCodeGenerationTests : UnitTestSiloHost
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public async Task RuntimeCodeGenTest()
        {
            var grain = this.GrainFactory.GetGrain<IRuntimeCodeGenGrain<@event>>(Guid.NewGuid());
            var expected = new @event
            {
                Id = Guid.NewGuid(),
                @if = new List<@event> { new @event { Id = Guid.NewGuid() } },
                PrivateId = Guid.NewGuid(),
                @public = new @event { Id = Guid.NewGuid() }
            };

            var actual = await grain.SetState(expected);
            Assert.IsNotNull(actual, "Result of SetState should be a non-null value.");
            Assert.IsTrue(expected.Equals(actual));

            var newActual = await grain.@static();
            Assert.IsTrue(expected.Equals(newActual), "Result of @static() should be equal to expected value.");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public async Task RuntimeCodeGenNestedGenericTest()
        {
            const int Expected = 123985;
            var grain = this.GrainFactory.GetGrain<INestedGenericGrain>(Guid.NewGuid());
            
            var nestedGeneric = new NestedGeneric<int> { Payload = new NestedGeneric<int>.Nested { Value = Expected } };
            var actual = await grain.Do(nestedGeneric);
            Assert.AreEqual(Expected, actual, "NestedGeneric<int>.Nested value should round-trip correctly.");
            
            var nestedConstructedGeneric = new NestedConstructedGeneric
            {
                Payload = new NestedConstructedGeneric.Nested<int> { Value = Expected }
            };
            actual = await grain.Do(nestedConstructedGeneric);
            Assert.AreEqual(Expected, actual, "NestedConstructedGeneric.Nested<int> value should round-trip correctly.");
        }
    }
}
