namespace Orleans.Messaging.Tests;

/// <summary>Asserts which case a <see cref="DeliveryResult"/> holds.</summary>
internal static class DeliveryResultAssert
{
    public static T IsCase<T>(DeliveryResult result) where T : class => Xunit.Assert.IsType<T>(result.Value);
}
