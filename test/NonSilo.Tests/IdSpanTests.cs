using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests
{
    /// <summary>
    /// Tests for IdSpan, a primitive type for identities representing a sequence of bytes.
    /// Validates equality, hash code consistency, and proper handling of null vs empty arrays.
    /// </summary>
    [TestCategory("BVT")]
    public class IdSpanTests
    {
        /// <summary>
        /// Tests that IdSpan.Create(string.Empty) and default(IdSpan) are NOT equal.
        /// They should have different internal states (empty array vs null) and should not be considered equal.
        /// </summary>
        [Fact]
        public void IdSpan_CreateEmptyString_NotEqualToDefault()
        {
            IdSpan createdFromEmptyString = IdSpan.Create(string.Empty);
            IdSpan defaultIdSpan = default;

            Assert.NotEqual(createdFromEmptyString, defaultIdSpan);
            Assert.False(createdFromEmptyString.Equals(defaultIdSpan));
            Assert.False(createdFromEmptyString == defaultIdSpan);
            Assert.True(createdFromEmptyString != defaultIdSpan);
        }

        /// <summary>
        /// Tests that hash codes are consistent with equality.
        /// If two IdSpans are equal, they must have the same hash code.
        /// </summary>
        [Fact]
        public void IdSpan_HashCode_ConsistentWithEquality()
        {
            IdSpan id1 = IdSpan.Create("test");
            IdSpan id2 = IdSpan.Create("test");
            IdSpan id3 = IdSpan.Create("different");

            // Equal objects must have equal hash codes
            Assert.Equal(id1, id2);
            Assert.Equal(id1.GetHashCode(), id2.GetHashCode());

            // Not equal objects may have different hash codes (not required but expected)
            Assert.NotEqual(id1, id3);
        }

        /// <summary>
        /// Tests that default IdSpan has expected properties.
        /// </summary>
        [Fact]
        public void IdSpan_Default_HasExpectedProperties()
        {
            IdSpan defaultIdSpan = default;

            Assert.True(defaultIdSpan.IsDefault);
            Assert.Equal(0, defaultIdSpan.GetHashCode());
            Assert.Equal("", defaultIdSpan.ToString());
        }

        /// <summary>
        /// Tests that IdSpan created from empty string has expected properties.
        /// </summary>
        [Fact]
        public void IdSpan_CreateEmptyString_HasExpectedProperties()
        {
            IdSpan emptyStringIdSpan = IdSpan.Create(string.Empty);

            Assert.True(emptyStringIdSpan.IsDefault);
            Assert.Equal("", emptyStringIdSpan.ToString());
            // Hash code should be computed from empty byte array, not 0
            Assert.NotEqual(0, emptyStringIdSpan.GetHashCode());
        }

        /// <summary>
        /// Tests that IdSpans with same content are equal.
        /// </summary>
        [Fact]
        public void IdSpan_SameContent_AreEqual()
        {
            IdSpan id1 = IdSpan.Create("test123");
            IdSpan id2 = IdSpan.Create("test123");

            Assert.Equal(id1, id2);
            Assert.True(id1 == id2);
            Assert.False(id1 != id2);
            Assert.Equal(id1.GetHashCode(), id2.GetHashCode());
        }

        /// <summary>
        /// Tests that IdSpans with different content are not equal.
        /// </summary>
        [Fact]
        public void IdSpan_DifferentContent_AreNotEqual()
        {
            IdSpan id1 = IdSpan.Create("test1");
            IdSpan id2 = IdSpan.Create("test2");

            Assert.NotEqual(id1, id2);
            Assert.False(id1 == id2);
            Assert.True(id1 != id2);
        }

        /// <summary>
        /// Tests CompareTo behavior with null and empty arrays.
        /// </summary>
        [Fact]
        public void IdSpan_CompareTo_HandlesNullAndEmpty()
        {
            IdSpan defaultIdSpan = default;
            IdSpan emptyStringIdSpan = IdSpan.Create(string.Empty);
            IdSpan normalIdSpan = IdSpan.Create("test");

            // Default (null) should compare differently than empty string
            Assert.NotEqual(0, defaultIdSpan.CompareTo(emptyStringIdSpan));
            Assert.NotEqual(0, emptyStringIdSpan.CompareTo(defaultIdSpan));
            
            // Both should be less than a normal span
            Assert.True(defaultIdSpan.CompareTo(normalIdSpan) < 0);
            Assert.True(emptyStringIdSpan.CompareTo(normalIdSpan) < 0);
        }
    }
}
