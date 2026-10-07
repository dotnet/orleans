using System.Reflection;

namespace Orleans.Dissemination.PerformanceHarness;

internal static class PublisherApi
{
    public static MethodInfo GetPublishMethod(Type publisherType)
    {
        var method = publisherType.GetMethod(
            "PublishStatistics", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancellationToken)]);
        if (method is null || method.ReturnType != typeof(Task))
        {
            throw Incompatible(publisherType, "Task PublishStatistics(CancellationToken)");
        }

        return method;
    }

    public static FieldInfo GetTimerField(Type publisherType)
    {
        var field = publisherType.GetField("_publishTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null || !typeof(IDisposable).IsAssignableFrom(field.FieldType))
        {
            throw Incompatible(publisherType, "IDisposable _publishTimer");
        }

        return field;
    }

    private static InvalidOperationException Incompatible(Type publisherType, string member) =>
        new($"Selected runtime '{publisherType.Assembly.FullName}' must provide {publisherType.FullName}.{member}. "
            + "Update the performance harness runtime adapter for this candidate.");
}
