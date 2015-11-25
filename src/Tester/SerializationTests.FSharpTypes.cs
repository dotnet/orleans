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
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;
using System.Collections.Generic;
using Orleans.TestingHost;
using System.Runtime.Serialization;

using UnitTests.FSharpTypes;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Collections;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for SerializationTests
    /// </summary>
    [TestClass]
    public class SerializationTestsFsharpTypes
    {
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        [TestMethod, /*TestCategory("BVT"),*/ TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_IntOption_Some()
        {
            var input = FSharpOption<int>.Some(0);
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.IsTrue(output.Equals(input));
        }

        [TestMethod, /*TestCategory("BVT"),*/ TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_IntOption_None()
        {
            var input = FSharpOption<int>.None;
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.IsTrue(output.Equals(input));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofInt()
        {
            var input = Record.ofInt(1);
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.IsTrue(output.Equals(input));
        }

        [TestMethod, /*TestCategory("BVT"),*/ TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofIntOption_Some()
        {
            var input = RecordOfIntOption.ofInt(1);
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.IsTrue(output.Equals(input));
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("FSharp"), TestCategory("Serialization")]
        public void SerializationTests_FSharp_Record_ofIntOption_None()
        {
            var input = RecordOfIntOption.Empty;
            var output = SerializationManager.RoundTripSerializationForTesting(input);
            Assert.IsTrue(output.Equals(input));
        }
    }
}
