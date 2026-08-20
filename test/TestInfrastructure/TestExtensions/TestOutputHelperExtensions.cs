namespace Xunit
{
    public static class TestOutputHelperExtensions
    {
        public static void WriteLine(this ITestOutputHelper output, object value)
        {
            output.WriteLine("{0}", value);
        }
    }
}
