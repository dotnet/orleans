namespace Orleans.Messaging.Tests.Support;

internal static class HierarchicalKeyTestExtensions
{
    public static int CountSegments(this HierarchicalKey key)
    {
        var count = 0;
        foreach (var _ in key)
        {
            count++;
        }

        return count;
    }
}
