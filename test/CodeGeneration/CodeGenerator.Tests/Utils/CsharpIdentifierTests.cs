using Orleans.CodeGenerator.Utilities;
using Xunit;

namespace CodeGenerator.Tests.Utils
{
    [Trait("Category", "BVT")]
    public class CSharpIdentifierTests
    {
        [Theory]
        [InlineData("Orleans.Core","Orleans_Core")]
        [InlineData("1NameWithInvalidStartCharacter", "_1NameWithInvalidStartCharacter")]
        [InlineData(" NameWithInvalidStartCharacter", "_NameWithInvalidStartCharacter")]
        [InlineData("Name with *illegal))characters", "Name_with__illegal__characters")]
        public void SanitizeClassNameTest(string inputName, string expected)
        {
            var sanitized = CSharpIdentifier.SanitizeClassName(inputName);
            Assert.Equal(expected, sanitized);
        }
    }
}
