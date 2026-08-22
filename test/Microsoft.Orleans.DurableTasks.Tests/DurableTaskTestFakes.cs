#nullable enable
using System;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Additional shared, hand-written test fakes for Phase 1 of the DurableTasks test plan.
/// Kept in a separate file from <c>TestFakes.cs</c> to avoid touching that file (which is shared across
/// concurrently-implemented phases).
/// </summary>

/// <summary>
/// Minimal concrete <see cref="IDurableTaskRequest"/> implementation which is genuinely serializable
/// (via <c>[GenerateSerializer]</c> code generation), unlike an NSubstitute-based proxy, which cannot be
/// deep-copied by the Orleans serializer (there is no registered codec/copier for a dynamically-generated
/// proxy type). This allows exercising storage code paths (e.g. <c>SetRequest</c>) which perform a real
/// deep-copy of the containing <c>DurableTaskState</c>.
/// </summary>
[GenerateSerializer]
[Alias("Microsoft.Orleans.DurableTasks.Tests.TestDurableTaskRequest")]
internal sealed class TestDurableTaskRequest : IDurableTaskRequest
{
    [Id(0)]
    public DurableTaskRequestContext? Context { get; set; }

    [Id(1)]
    public InvokeMethodOptions Options { get; private set; }

    public void AddInvokeMethodOptions(InvokeMethodOptions options) => Options |= options;

    public object? GetTarget() => null;

    public void SetTarget(ITargetHolder holder)
    {
    }

    public ValueTask<Response> Invoke() => throw new NotSupportedException("Not invokable in tests.");

    public int GetArgumentCount() => 0;

    public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

    public string GetMethodName() => "TestMethod";

    public string GetInterfaceName() => "Microsoft.Orleans.DurableTasks.Tests.ITestInterface";

    public string GetActivityName() => "Microsoft.Orleans.DurableTasks.Tests.ITestInterface/TestMethod";

    public MethodInfo GetMethod() => typeof(TestDurableTaskRequest).GetMethod(nameof(GetMethod))!;

    public Type GetInterfaceType() => typeof(TestDurableTaskRequest);

    public void Dispose()
    {
    }

    public DurableTask CreateTask() => DurableTask.Run(static _ => { });
}

/// <summary>
/// A <see cref="GrainReference"/> subclass which also implements <see cref="IDurableTaskObserver"/>,
/// used to exercise <c>DurableTaskState.MigrateLegacyObservers</c>'s <c>observer is GrainReference</c> branch
/// without requiring the full Orleans grain-reference-proxy code generation pipeline.
/// </summary>
internal sealed class FakeGrainReferenceObserver(GrainReferenceShared shared, IdSpan key) : GrainReference(shared, key), IDurableTaskObserver
{
    public ValueTask OnResponseAsync(TaskId taskId, DurableTaskResponse response, CancellationToken cancellationToken = default) => default;

    /// <summary>
    /// Creates a new <see cref="FakeGrainReferenceObserver"/> whose <see cref="GrainReference.GrainId"/> is
    /// derived from <paramref name="grainType"/> and <paramref name="key"/>, backed by real serializer
    /// infrastructure resolved from <paramref name="serviceProvider"/>.
    /// </summary>
    public static FakeGrainReferenceObserver Create(IServiceProvider serviceProvider, string grainType, string key)
    {
        var runtime = NSubstitute.Substitute.For<IGrainReferenceRuntime>();
        var codecProvider = serviceProvider.GetRequiredService<CodecProvider>();
        var copyContextPool = serviceProvider.GetRequiredService<CopyContextPool>();
        var shared = new GrainReferenceShared(
            GrainType.Create(grainType),
            GrainInterfaceType.Create("Microsoft.Orleans.DurableTasks.Tests.IDurableTaskObserverGrainExtension"),
            interfaceVersion: 0,
            runtime,
            InvokeMethodOptions.None,
            codecProvider,
            copyContextPool,
            serviceProvider);
        return new FakeGrainReferenceObserver(shared, IdSpan.Create(key));
    }
}
