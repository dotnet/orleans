namespace Orleans.Persistence.TestKit;

internal static class StorageTestAssertions
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but found '{actual}'.");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new InvalidOperationException($"Expected a value other than '{notExpected}'.");
        }
    }

    public static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected the condition to be true.");
        }
    }

    public static void False(bool condition)
    {
        if (condition)
        {
            throw new InvalidOperationException("Expected the condition to be false.");
        }
    }

    public static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, but found '{value}'.");
        }
    }

    public static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }

    public static T IsType<T>(object? value)
    {
        if (value?.GetType() != typeof(T))
        {
            throw new InvalidOperationException($"Expected a value of type '{typeof(T)}', but found '{value?.GetType()}'.");
        }

        return (T)value;
    }

    public static T IsAssignableFrom<T>(object? value)
    {
        if (value is not T result)
        {
            throw new InvalidOperationException($"Expected a value assignable to '{typeof(T)}', but found '{value?.GetType()}'.");
        }

        return result;
    }
}

internal static class StorageTestExecution
{
    public static async Task<Exception?> ExceptionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
