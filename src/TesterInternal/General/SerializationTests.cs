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
    public class SerializationTests
    {
        /// <summary>
        /// Initializes the system for testing.
        /// </summary>
        [TestInitialize]
        public void InitializeForTesting()
        {
            SerializationManager.InitializeForTesting();
        }

        /// <summary>
        /// Tests that grain references are serialized correctly.
        /// </summary>
        [TestMethod]
        [TestCategory("BVT")]
        [TestCategory("Functional")]
        [TestCategory("Serialization")]
        public void SerializationTests_GrainReference()
        {
            this.RunGrainReferenceSerializationTest<ISimpleGrain>();
        }

        /// <summary>
        /// Tests that generic grain references are serialized correctly.
        /// </summary>
        [TestMethod]
        [TestCategory("BVT")]
        [TestCategory("Functional")]
        [TestCategory("Serialization")]
        public void SerializationTests_GenericGrainReference()
        {
            this.RunGrainReferenceSerializationTest<ISimpleGenericGrain1<int>>();
        }

        private void RunGrainReferenceSerializationTest<TGrainInterface>()
        {
            var counters = new List<CounterStatistic>
            {
                CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_SERIALIZATION),
                CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DESERIALIZATION),
                CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BODY_FALLBACK_DEEPCOPIES)
            };

            // Get the (generated) grain reference implementation for a particular grain interface type.
            var grainReference = this.GetGrainReference<TGrainInterface>();

            // Get the current value of each of the fallback serialization counters.
            var initial = counters.Select(_ => _.GetCurrentValue()).ToList();

            // Serialize and deserialize the grain reference.
            var writer = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(grainReference, writer);
            var deserialized = SerializationManager.Deserialize(new BinaryTokenStreamReader(writer.ToByteArray()));
            var copy = (GrainReference)SerializationManager.DeepCopy(deserialized);

            // Get the final value of the fallback serialization counters.
            var final = counters.Select(_ => _.GetCurrentValue()).ToList();

            // Ensure that serialization was correctly performed.
            Assert.IsInstanceOfType(
                deserialized,
                grainReference.GetType(),
                "Deserialized GrainReference type should match original type");
            var deserializedGrainReference = (GrainReference)deserialized;
            Assert.IsTrue(
                deserializedGrainReference.GrainId.Equals(grainReference.GrainId),
                "Deserialized GrainReference should have same GrainId as original value.");
            Assert.IsInstanceOfType(copy, grainReference.GetType(), "DeepCopy GrainReference type should match original type");
            Assert.IsTrue(
                copy.GrainId.Equals(grainReference.GrainId),
                "DeepCopy GrainReference should have same GrainId as original value.");

            // Check that the counters have not changed.
            var initialString = string.Join(",", initial);
            var finalString = string.Join(",", final);
            Assert.AreEqual(initialString, finalString, "GrainReference serialization should not use fallback serializer.");
        }

        /// <summary>
        /// Returns a grain reference.
        /// </summary>
        /// <typeparam name="TGrainInterface">
        /// The grain interface type.
        /// </typeparam>
        /// <returns>
        /// The <see cref="GrainReference"/>.
        /// </returns>
        public GrainReference GetGrainReference<TGrainInterface>()
        {
            var grainType = typeof(TGrainInterface);

            if (typeof(TGrainInterface).IsGenericTypeDefinition)
            {
                throw new ArgumentException("Cannot create grain reference for non-concrete grain type");
            }

            if (typeof(TGrainInterface).IsConstructedGenericType)
            {
                grainType = typeof(TGrainInterface).GetGenericTypeDefinition();
            }

            var type = typeof(TGrainInterface).Assembly.DefinedTypes.First(
                _ =>
                {
                    var attr = _.GetCustomAttribute<GrainReferenceAttribute>();
                    return attr != null && attr.GrainType == grainType;
                }).AsType();

            if (typeof(TGrainInterface).IsConstructedGenericType)
            {
                type = type.MakeGenericType(typeof(TGrainInterface).GetGenericArguments());
            }

            var regularGrainId = GrainId.GetGrainIdForTesting(Guid.NewGuid());
            var grainRef = GrainReference.FromGrainId(regularGrainId);
            return
                (GrainReference)
                type.GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.CreateInstance,
                    null,
                    new[] { typeof(GrainReference) },
                    null).Invoke(new object[] { grainRef });
        }
    }
}
