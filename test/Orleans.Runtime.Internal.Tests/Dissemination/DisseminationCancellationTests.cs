using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Metadata;
using Orleans.Runtime.Scheduler;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public sealed class DisseminationCancellationTests
{
    [Theory]
    [InlineData(typeof(IDisseminationSystemTarget))]
    [InlineData(typeof(IClusterManifestSystemTarget))]
    [InlineData(typeof(ISiloManifestSystemTarget))]
    [InlineData(typeof(IDeploymentLoadPublisher))]
    [InlineData(typeof(IMembershipService))]
    public void RuntimeRpcMethodsExposeCancellationToken(Type interfaceType)
    {
        foreach (var method in interfaceType.GetMethods())
        {
            Assert.Equal(typeof(CancellationToken), method.GetParameters().LastOrDefault()?.ParameterType);
        }
    }

    [Theory]
    [InlineData(typeof(DisseminationProtocol))]
    [InlineData(typeof(DisseminationBroadcastQueue))]
    [InlineData(typeof(DisseminationSystemTarget))]
    [InlineData(typeof(DisseminationMembership))]
    [InlineData(typeof(MembershipDisseminationNamespace))]
    [InlineData(typeof(DeploymentLoadStatisticsDisseminationNamespace))]
    [InlineData(typeof(DeploymentLoadPublisher))]
    [InlineData(typeof(WakeTimer))]
    [InlineData(typeof(MembershipAgent))]
    [InlineData(typeof(MembershipGossiper))]
    [InlineData(typeof(MembershipSystemTarget))]
    [InlineData(typeof(MembershipTableManager))]
    [InlineData(typeof(RemoteSiloProber))]
    [InlineData(typeof(ClusterManifestProvider))]
    [InlineData(typeof(ClusterManifestSystemTarget))]
    public void DisseminationAsyncMethodsExposeCancellationToken(Type componentType)
    {
        var types = componentType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Prepend(componentType);
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var returnType = method.ReturnType;
                var isAsync = typeof(Task).IsAssignableFrom(returnType)
                    || returnType == typeof(ValueTask)
                    || returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
                if (!isAsync || method.IsSpecialName || method.Name.StartsWith('<'))
                {
                    continue;
                }

                // IAsyncDisposable defines the parameterless cleanup boundary.
                if (method.Name == nameof(IAsyncDisposable.DisposeAsync) && typeof(IAsyncDisposable).IsAssignableFrom(type))
                {
                    continue;
                }

                Assert.True(
                    method.GetParameters().Any(static parameter => parameter.ParameterType == typeof(CancellationToken)),
                    $"{type.FullName}.{method.Name} must expose a CancellationToken parameter.");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledPublicationPropagatesCallerCancellation(bool cancelBeforeScheduling)
    {
        var context = Substitute.For<IGrainContext>();
        using var cancellation = new CancellationTokenSource();
        if (cancelBeforeScheduling)
        {
            cancellation.Cancel();
        }

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;
        CancellationToken observedToken = default;
        Task<int> operation;
        RuntimeContext.SetExecutionContext(context, out var previous);
        try
        {
            operation = context.RunOrQueueTask(token =>
            {
                invoked = true;
                observedToken = token;
                return completion.Task;
            }, cancellation.Token);
        }
        finally
        {
            RuntimeContext.ResetExecutionContext(previous);
        }

        try
        {
            Assert.Equal(!cancelBeforeScheduling, invoked);
            if (invoked)
            {
                Assert.Equal(cancellation.Token, observedToken);
            }

            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        finally
        {
            completion.TrySetResult(1);
        }
    }
}
