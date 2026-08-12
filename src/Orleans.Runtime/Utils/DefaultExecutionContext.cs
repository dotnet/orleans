using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

/// <summary>
/// Provides an <see cref="ExecutionContext"/> which contains no ambient state.
/// </summary>
internal static class DefaultExecutionContext
{
    public static ExecutionContext Instance { get; } = GetInstance();

    private static ExecutionContext GetInstance()
    {
        try
        {
            return GetRuntimeDefault(null!);
        }
        catch (MissingFieldException)
        {
            return CaptureDefault();
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Default")]
    private static extern ref ExecutionContext GetRuntimeDefault(ExecutionContext _);

    internal static ExecutionContext CaptureDefault()
    {
        Task<ExecutionContext> captureTask;
        if (ExecutionContext.IsFlowSuppressed())
        {
            captureTask = CaptureAsync();
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                captureTask = CaptureAsync();
            }
        }

        return captureTask.GetAwaiter().GetResult();

        static Task<ExecutionContext> CaptureAsync() => Task.Run(
            static () => ExecutionContext.Capture()
                ?? throw new InvalidOperationException("Could not capture the default execution context."));
    }
}
