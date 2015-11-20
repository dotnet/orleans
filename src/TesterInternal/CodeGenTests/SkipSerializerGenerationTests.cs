using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tester.CodeGenTests
{
    using System.Reflection;
    using Orleans.Serialization;

    [TestClass]
    public class SkipSerializerGenerationTests
    {
        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SkipSerializerGenerationAttribute_VerifyTypeWithoutAttribute()
        {
            var type = typeof(AllowSerializerGeneration).GetTypeInfo();
            var result = TypeUtilities.IsTypeIsInaccessibleForSerialization(type, type.Module, type.Assembly);
            Assert.IsFalse(result, "The type should have been able to have a serializer generated");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Serialization")]
        public void SkipSerializerGenerationAttribute_VerifyTypeWithAttribute()
        {
            var type = typeof(DisallowSerializerGeneration).GetTypeInfo();
            var result = TypeUtilities.IsTypeIsInaccessibleForSerialization(type, type.Module, type.Assembly);
            Assert.IsTrue(result, "Serializer generation should have been blocked");
        }

        public class AllowSerializerGeneration
        {
            private int i = 0;
        }

        [SkipSerializerGeneration]
        public class DisallowSerializerGeneration : AllowSerializerGeneration
        {
        }
    }
}
