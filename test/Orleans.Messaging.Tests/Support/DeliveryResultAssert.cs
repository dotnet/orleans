namespace Orleans.Messaging.Tests;

/// <summary>Asserts which case a <see cref="DeliveryResult"/> holds.</summary>
internal static class DeliveryResultAssert
{
    public static T IsCase<T>(DeliveryResult result) where T : class => Xunit.Assert.IsType<T>(result.Value);

    /// <summary>Asserts that two results hold the same case with the same data; cases compare by reference.</summary>
    public static void Equal(DeliveryResult expected, DeliveryResult actual) => Xunit.Assert.Equal(Key(expected), Key(actual));

    /// <summary>Asserts that two sequences of results hold the same cases with the same data, in order.</summary>
    public static void Equal(IEnumerable<DeliveryResult> expected, IEnumerable<DeliveryResult> actual) =>
        Xunit.Assert.Equal(expected.Select(Key), actual.Select(Key));

    /// <summary>Asserts that <paramref name="actual"/> contains a result with the same case and data as <paramref name="expected"/>.</summary>
    public static void Contains(DeliveryResult expected, IEnumerable<DeliveryResult> actual) =>
        Xunit.Assert.Contains(Key(expected), actual.Select(Key));

    // Compares the case and its fields structurally, so null and empty codes, and separators within fields, stay distinct.
    private static (Type Case, string? Reason, string? Code) Key(DeliveryResult result) => result switch
    {
        DeliveryResult.DeadLettered { Reason: var reason, Code: var code } => (typeof(DeliveryResult.DeadLettered), reason, code),
        _ => (result.Value!.GetType(), null, null)
    };
}
