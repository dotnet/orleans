namespace Xunit.Abstractions
{
    public static class TestOutputHelperExtensions
    {
        public static void WriteLine(this ITestOutputHelper output, object value)
        {
            output.WriteLine(value.ToString());
        }
    }
}
