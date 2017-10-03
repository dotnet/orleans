using System.Reflection;

namespace UnitTests.Grains
{
    public static class TestGrains
    {
        public static Assembly Assembly => typeof(TestGrains).Assembly;
    }
}
