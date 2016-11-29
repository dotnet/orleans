using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace Tester.CodeGenTests
{
    /// <summary>
    /// Tests runtime code generation.
    /// </summary>
    public class RuntimeCodeGenerationTests : OrleansTestingBase, IClassFixture<DefaultClusterFixture>
    {
        public RuntimeCodeGenerationTests()
        {
            SerializationTestEnvironment.Initialize();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public async Task RuntimeCodeGenTest()
        {
            var grain = GrainFactory.GetGrain<IRuntimeCodeGenGrain<@event>>(Guid.NewGuid());
            var expected = new @event
            {
                Id = Guid.NewGuid(),
                @if = new List<@event> { new @event { Id = Guid.NewGuid() } },
                PrivateId = Guid.NewGuid(),
                @public = new @event { Id = Guid.NewGuid() },
                Enum = @event.@enum.@int
            };

            var actual = await grain.SetState(expected);
            Assert.NotNull(actual);

            Assert.True(expected.Equals(actual));

            var newActual = await grain.@static();
            Assert.True(expected.Equals(newActual), "Result of @static() should be equal to expected value.");
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen")]
        public async Task RuntimeCodeGenNestedGenericTest()
        {
            const int Expected = 123985;
            var grain = GrainFactory.GetGrain<INestedGenericGrain>(Guid.NewGuid());
            
            var nestedGeneric = new NestedGeneric<int> { Payload = new NestedGeneric<int>.Nested { Value = Expected } };
            var actual = await grain.Do(nestedGeneric);
            Assert.Equal(Expected, actual); // NestedGeneric<int>.Nested value should round-trip correctly.

            var nestedConstructedGeneric = new NestedConstructedGeneric
            {
                Payload = new NestedConstructedGeneric.Nested<int> { Value = Expected }
            };
            actual = await grain.Do(nestedConstructedGeneric);
            Assert.Equal(Expected, actual); // NestedConstructedGeneric.Nested<int> value should round-trip correctly.
        }
    }
}
