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
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Streams;

namespace UnitTests.OrleansRuntime.Streams
{
    [TestClass]
    public class SubscriptionMarkerTests
    {
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public void MarkAsImplicitSubscriptionTest()
        {
            Guid guid = Guid.Empty;

            Assert.IsFalse(SubscriptionMarker.IsImplicitSubscription(guid));

            Guid markedGuid = SubscriptionMarker.MarkAsImplictSubscriptionId(guid);

            Assert.IsTrue(SubscriptionMarker.IsImplicitSubscription(markedGuid));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public void MarkAsExplicitSubscriptionTest()
        {
            byte[] guidBytes = Enumerable.Range(0, 16).Select(i => (byte)0xff).ToArray();
            var guid = new Guid(guidBytes);

            Assert.IsTrue(SubscriptionMarker.IsImplicitSubscription(guid));

            Guid markedGuid = SubscriptionMarker.MarkAsExplicitSubscriptionId(guid);

            Assert.IsFalse(SubscriptionMarker.IsImplicitSubscription(markedGuid));
        }
    }
}
