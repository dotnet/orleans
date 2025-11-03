using Orleans.Dashboard.Metrics.TypeFormatting;
using Xunit;

namespace UnitTests
{
    public class TypeFormatterTests
    {
        [Fact]
        public void TestSimpleType()
        {
            var example = "System.String";

            var name = TypeFormatter.Parse(example);

            Assert.Equal("String", name);
        }

        [Fact]
        public void TestCustomType()
        {
            var example = "ExecuteAsync(CreateApp)";

            var name = TypeFormatter.Parse(example);

            Assert.Equal("ExecuteAsync(CreateApp)", name);
        }

        [Fact]
        public void TestFriendlyNameForStrings()
        {
            var example = "TestGrains.GenericGrain`1[[System.String,mscorlib]]";

            var name = TypeFormatter.Parse(example);

            Assert.Equal("TestGrains.GenericGrain<String>", name);
        }

        [Fact]
        public void TestGenericWithMultipleTs()
        {
            var example = "TestGrains.IGenericGrain`1[[System.Tuple`2[[string],[int]]]]";

            var name = TypeFormatter.Parse(example);

            Assert.Equal("TestGrains.IGenericGrain<Tuple<string, int>>", name);
        }

        [Fact]
        public void TestGenericGrainWithMultipleTs()
        {
            var example = "TestGrains.ITestGenericGrain`2[[string],[int]]";

            var name = TypeFormatter.Parse(example);

            Assert.Equal("TestGrains.ITestGenericGrain<string, int>", name);
        }

        [Fact]
        public void TestGenericGrainWithFsType()
        {
            var example = ".Program.Progress";

            var name = TypeFormatter.Parse(example);

            Assert.Equal(".Program.Progress", name);
        }
    }
}