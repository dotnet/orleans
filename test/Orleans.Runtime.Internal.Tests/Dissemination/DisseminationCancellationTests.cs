using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dissemination")]
public sealed class DisseminationCancellationTests
{
    [Theory]
    [InlineData(typeof(IDisseminationSystemTarget))]
    public void DisseminationRpcMethodsExposeRequiredCancellationToken(Type interfaceType)
    {
        foreach (var method in interfaceType.GetMethods())
        {
            Assert.Equal(typeof(CancellationToken), method.GetParameters().LastOrDefault()?.ParameterType);
            Assert.False(method.GetParameters()[^1].IsOptional);
        }
    }

    [Theory]
    [InlineData(typeof(DisseminationProtocol))]
    [InlineData(typeof(DisseminationBroadcastQueue))]
    [InlineData(typeof(DisseminationSendGate))]
    [InlineData(typeof(DisseminationSystemTarget))]
    [InlineData(typeof(DisseminationMembership))]
    [InlineData(typeof(MembershipDisseminationNamespace))]
    [InlineData(typeof(DeploymentLoadStatisticsDisseminationNamespace))]
    [InlineData(typeof(WakeTimer))]
    public void DisseminationAsyncMethodsExposeRequiredCancellationToken(Type componentType)
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
                Assert.All(
                    method.GetParameters().Where(static parameter => parameter.ParameterType == typeof(CancellationToken)),
                    static parameter => Assert.False(parameter.IsOptional));
            }
        }
    }
}
