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

using System.Runtime.Serialization;
using Orleans.Runtime.Configuration;
using UnitTests.SerializerTests;

namespace UnitTests.General
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using Orleans.CodeGeneration;
    using Orleans.Runtime;
    using Orleans.Serialization;

    using UnitTests.GrainInterfaces;

    /// <summary>
    /// Tests for the serialization system.
    /// </summary>
    [TestClass]
    public class FallbackBuiltInSerializationTests : BuiltInSerializerTests
    {
        /// <summary>
        /// Initializes the system for testing.
        /// </summary>
        [TestInitialize]
        public new void InitializeForTesting()
        {
            TraceLogger.Initialize(new NodeConfiguration());
            SerializationManager.Initialize(false, null, true);
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
        }

        public override void Serialize_Predicate()
        {
            // there's no ability to serialize expressions with Json.Net serializer yet.
        }

        public override void Serialize_Func()
        {
            // there's no ability to serialize expressions with Json.Net serializer yet.
        }
    }
}
