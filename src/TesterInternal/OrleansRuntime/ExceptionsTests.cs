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

using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace UnitTests.OrleansRuntime
{
    [TestClass]
    public class ExceptionsTests
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_DotNet()
        {
            ActivationAddress activationAddress = ActivationAddress.NewActivationAddress(SiloAddress.NewLocalAddress(12345), GrainId.NewId());
            SiloAddress primaryDirectoryForGrain = SiloAddress.NewLocalAddress(6789);
           
            Catalog.DuplicateActivationException original = new Catalog.DuplicateActivationException(activationAddress, primaryDirectoryForGrain);
            Catalog.DuplicateActivationException output = TestingUtils.RoundTripDotNetSerializer(original);

            Assert.AreEqual(original.Message, output.Message);
            Assert.AreEqual(original.ActivationToUse, output.ActivationToUse);
            Assert.AreEqual(original.PrimaryDirectoryForGrain, output.PrimaryDirectoryForGrain);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Serialization")]
        public void SerializationTests_Exception_Orleans()
        {
            ActivationAddress activationAddress = ActivationAddress.NewActivationAddress(SiloAddress.NewLocalAddress(12345), GrainId.NewId());
            SiloAddress primaryDirectoryForGrain = SiloAddress.NewLocalAddress(6789);

            Catalog.DuplicateActivationException original = new Catalog.DuplicateActivationException(activationAddress, primaryDirectoryForGrain);
            Catalog.DuplicateActivationException output = SerializationManager.RoundTripSerializationForTesting(original);

            Assert.AreEqual(original.Message, output.Message);
            Assert.AreEqual(original.ActivationToUse, output.ActivationToUse);
            Assert.AreEqual(original.PrimaryDirectoryForGrain, output.PrimaryDirectoryForGrain);
        }
    }
}
