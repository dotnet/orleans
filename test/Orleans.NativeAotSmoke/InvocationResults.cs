using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;

using var services = new ServiceCollection()
    .AddGeneratedContext(new InvocationResultContext())
    .BuildServiceProvider();
var pool = services.GetRequiredService<CopyContextPool>();
foreach (var valueTask in new[] { false, true })
{
    foreach (var pending in new[] { false, true })
    {
        var original = new List<int> { 7, 9 };
        var completion = new TaskCompletionSource<List<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending) completion.SetResult(original);
        using IInvokable request = valueTask
            ? new ValueTaskResultRequest(completion.Task)
            : new TaskResultRequest(completion.Task);
        var invocation = request.Invoke(pool);
        if (pending) completion.SetResult(original);
        using var response = await invocation;
        var result = response.GetResult<List<int>>();
        if (result is null || ReferenceEquals(result, original) || !result.SequenceEqual(original))
        {
            throw new InvalidOperationException("Invocation must copy its result using the registered result copier.");
        }

        result.Add(11);
        if (original.Count != 2)
        {
            throw new InvalidOperationException("The invocation result still aliases the target's state.");
        }
    }
}

Console.WriteLine("Task and ValueTask invocation results copied with closed result factories under NativeAOT.");

// Only the result graph is registered: copying must not resolve a Response<List<int>> copier.
[GeneratedContextType<List<int>>]
internal partial class InvocationResultContext : GeneratedContext;

internal sealed class TaskResultRequest(Task<List<int>> result) : TaskRequest<List<int>>
{
    protected override Task<List<int>> InvokeInner() => result;
    public override object GetTarget() => this;
    public override void SetTarget(ITargetHolder holder) { }
    public override string GetMethodName() => nameof(InvokeInner);
    public override string GetInterfaceName() => nameof(TaskResultRequest);
    public override string GetActivityName() => nameof(TaskResultRequest);
    public override Type GetInterfaceType() => typeof(TaskResultRequest);
    public override MethodInfo GetMethod() => throw new NotSupportedException();
    public override void Dispose() { }
}

internal sealed class ValueTaskResultRequest(Task<List<int>> result) : Request<List<int>>
{
    protected override ValueTask<List<int>> InvokeInner() => new(result);
    public override object GetTarget() => this;
    public override void SetTarget(ITargetHolder holder) { }
    public override string GetMethodName() => nameof(InvokeInner);
    public override string GetInterfaceName() => nameof(ValueTaskResultRequest);
    public override string GetActivityName() => nameof(ValueTaskResultRequest);
    public override Type GetInterfaceType() => typeof(ValueTaskResultRequest);
    public override MethodInfo GetMethod() => throw new NotSupportedException();
    public override void Dispose() { }
}
