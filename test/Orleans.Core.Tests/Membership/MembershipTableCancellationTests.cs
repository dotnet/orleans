using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using TestExtensions;
using Xunit;
using TypeConverter = Orleans.Serialization.TypeSystem.TypeConverter;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class MembershipTableCancellationTests
{
    public static TheoryData<string> Operations { get; } = new()
    {
        nameof(IMembershipTable.InitializeMembershipTableAsync),
        nameof(IMembershipTable.DeleteMembershipTableEntriesAsync),
        nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync),
        nameof(IMembershipTable.ReadRowAsync),
        nameof(IMembershipTable.ReadAllAsync),
        nameof(IMembershipTable.InsertRowAsync),
        nameof(IMembershipTable.UpdateRowAsync),
        nameof(IMembershipTable.UpdateIAmAliveAsync),
    };

    public static TheoryData<string, string> WireOperations { get; } = new()
    {
        { nameof(IMembershipTable.InitializeMembershipTableAsync), "FB89E5E9" },
        { nameof(IMembershipTable.DeleteMembershipTableEntriesAsync), "BF899C85" },
        { nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync), "7A519C2E" },
        { nameof(IMembershipTable.ReadRowAsync), "D851FB33" },
        { nameof(IMembershipTable.ReadAllAsync), "00BCE16F" },
        { nameof(IMembershipTable.InsertRowAsync), "FEF3AC5A" },
        { nameof(IMembershipTable.UpdateRowAsync), "E06D3DBC" },
        { nameof(IMembershipTable.UpdateIAmAliveAsync), "B1A52D2B" },
    };

    [Theory]
    [MemberData(nameof(WireOperations))]
    public void CancellationOverload_UsesLegacyWireIdentity(string methodName, string legacyId)
    {
        var method = Assert.Single(typeof(IMembershipTable).GetMethods(),
            candidate => candidate.Name == methodName && candidate.GetParameters().LastOrDefault()?.ParameterType == typeof(CancellationToken));
        var legacyMethod = typeof(IMembershipTable).GetMethod(
            methodName[..^"Async".Length],
            method.GetParameters().SkipLast(1).Select(parameter => parameter.ParameterType).ToArray());
        Assert.NotNull(legacyMethod);
        Assert.Contains(methodName, Assert.IsType<ObsoleteAttribute>(legacyMethod.GetCustomAttribute<ObsoleteAttribute>()).Message, StringComparison.Ordinal);
        Assert.Equal(legacyId, Assert.Single(method.GetCustomAttributes<AliasAttribute>()).Alias);
        var invokableType = Assert.Single(typeof(IMembershipTable).Assembly.GetTypes(),
            type => typeof(IInvokable).IsAssignableFrom(type)
                && type.GetCustomAttributes<CompoundTypeAliasAttribute>().Any(
                    alias => alias.Components.SequenceEqual(new object[] { "inv", typeof(GrainReference), typeof(IMembershipTable), legacyId })));
        using var invokable = Assert.IsAssignableFrom<IInvokable>(Activator.CreateInstance(invokableType));
        Assert.Equal(method, invokable.GetMethod());
        Assert.True(invokable.IsCancellable);
        Assert.Equal(typeof(IMembershipTable), invokable.GetInterfaceType());
    }

    [Theory]
    [MemberData(nameof(WireOperations))]
    public async Task GeneratedProxyCalls_PreserveLegacyWireCompatibility(string operation, string legacyId)
    {
        using var currentServices = CreateSerializerServices();
        using var legacyServices = CreateSerializerServices(useLegacyMetadata: true);
        var currentSerializer = currentServices.GetRequiredService<Serializer>();
        var legacySerializer = legacyServices.GetRequiredService<Serializer>();
        var currentConverter = currentServices.GetRequiredService<TypeConverter>();
        var legacyConverter = legacyServices.GetRequiredService<TypeConverter>();
        var fixture = new LegacyProvider();
        var arguments = ExpectedArguments(fixture, operation);
        var legacyMethodName = operation[..^"Async".Length];
        var legacyType = GetLegacyInvokerType(legacyId);
        var currentType = GetCurrentInvokerType(operation);
        using var cancellation = new CancellationTokenSource();
        using var legacyRequest = await CaptureProxyCall(currentServices, legacyMethodName, arguments);
        using var currentRequest = await CaptureProxyCall(currentServices, operation, [.. arguments, cancellation.Token]);

        Assert.Equal(legacyType, legacyRequest.GetType());
        Assert.Equal(legacyMethodName, legacyRequest.GetMethodName());
        Assert.False(legacyRequest.IsCancellable);
        Assert.Equal($"{legacyType.FullName},Orleans.Core", currentConverter.Format(legacyType));
        Assert.Equal(currentType, currentRequest.GetType());
        Assert.Equal(operation, currentRequest.GetMethodName());
        Assert.Equal(cancellation.Token, currentRequest.GetCancellationToken());
        Assert.Equal(GetLegacyAlias(legacyId), currentConverter.Format(currentType));
        Assert.Equal(GetLegacyAlias(legacyId), legacyConverter.Format(legacyType));
        Assert.Equal(currentType, currentConverter.Parse(GetLegacyAlias(legacyId)));
        Assert.Equal(legacyType, legacyConverter.Parse(GetLegacyAlias(legacyId)));

        var oldSenderBytes = legacySerializer.SerializeToArray<object>(legacyRequest);
        var asyncSenderBytes = currentSerializer.SerializeToArray<object>(currentRequest);
        var tokenlessSenderBytes = currentSerializer.SerializeToArray<object>(legacyRequest);
        Assert.Equal(oldSenderBytes, asyncSenderBytes);
        cancellation.Cancel();
        Assert.Equal(asyncSenderBytes, currentSerializer.SerializeToArray<object>(currentRequest));

        await AssertReceivedCall(currentSerializer, oldSenderBytes, currentType);
        await AssertReceivedCall(legacySerializer, oldSenderBytes, legacyType);
        await AssertReceivedCall(currentSerializer, asyncSenderBytes, currentType);
        await AssertReceivedCall(legacySerializer, asyncSenderBytes, legacyType);
        await AssertReceivedCall(currentSerializer, tokenlessSenderBytes, legacyType);
        await AssertReceivedCall(legacySerializer, tokenlessSenderBytes, legacyType);

        async Task AssertReceivedCall(Serializer serializer, byte[] bytes, Type expectedType)
        {
            using var request = Assert.IsAssignableFrom<IInvokable>(serializer.Deserialize<object>(bytes));
            Assert.Equal(expectedType, request.GetType());
            Assert.Equal(expectedType == currentType ? operation : legacyMethodName, request.GetMethodName());
            Assert.Equal(arguments.Length + (expectedType == currentType ? 1 : 0), request.GetArgumentCount());
            Assert.Equal(CancellationToken.None, request.GetCancellationToken());
            AssertArguments(arguments, Enumerable.Range(0, arguments.Length).Select(request.GetArgument).ToArray());
            var target = new LegacyProvider();
            target.Completion.SetResult(true);
            request.SetTarget(new MembershipTargetHolder(target));

            using var response = await request.Invoke();

            Assert.Null(response.Exception);
            var call = Assert.Single(target.Calls);
            Assert.Equal(legacyMethodName, call.Method);
            AssertArguments(arguments, call.Arguments);
            object? expectedResult = operation switch
            {
                nameof(IMembershipTable.ReadAllAsync) or nameof(IMembershipTable.ReadRowAsync) => target.Data,
                nameof(IMembershipTable.InsertRowAsync) or nameof(IMembershipTable.UpdateRowAsync) => true,
                _ => null,
            };
            Assert.Equal(expectedResult, response.Result);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_PreservesLegacyArgumentsAndResult(string operation)
    {
        var provider = new LegacyProvider();
        provider.Completion.SetResult(true);

        var result = await Invoke(provider, operation, TestContext.Current.CancellationToken);

        var call = Assert.Single(provider.Calls);
        Assert.Equal(operation[..^"Async".Length], call.Method);
        Assert.Equal(ExpectedArguments(provider, operation), call.Arguments);
        object? expected = operation switch
        {
            nameof(IMembershipTable.ReadAllAsync) or nameof(IMembershipTable.ReadRowAsync) => provider.Data,
            nameof(IMembershipTable.InsertRowAsync) or nameof(IMembershipTable.UpdateRowAsync) => true,
            _ => null,
        };
        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_PreCanceled_SkipsLegacyOperation(string operation)
    {
        var provider = new LegacyProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(provider, operation, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(provider.Calls);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_CancelsWaitAndRetainsProviderLifetime(string operation)
    {
        var provider = new LegacyProvider();
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke(provider, operation, cancellation.Token);
        Assert.Single(provider.Calls);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(pending.IsCanceled);
        Assert.False(provider.Completion.Task.IsCompleted);
        provider.Completion.SetException(new InvalidOperationException("Late tokenless provider failure"));
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_PropagatesProviderFailure(string operation)
    {
        var provider = new LegacyProvider();
        var failure = new InvalidOperationException("Provider failure");
        provider.Completion.SetException(failure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Invoke(provider, operation, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task NativeCancellationOverride_ReceivesTokenAndOwnsResult()
    {
        var provider = new NativeProvider();
        IMembershipTable table = provider;
        using var cancellation = new CancellationTokenSource();

        var result = await table.ReadAllAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, provider.ReceivedToken);
        Assert.Same(provider.Data, result);
        Assert.Empty(provider.Calls);
    }

    private static async Task<object?> Invoke(LegacyProvider provider, string operation, CancellationToken cancellationToken)
    {
        IMembershipTable table = provider;
        switch (operation)
        {
            case nameof(IMembershipTable.InitializeMembershipTableAsync):
                await table.InitializeMembershipTableAsync(true, cancellationToken);
                break;
            case nameof(IMembershipTable.DeleteMembershipTableEntriesAsync):
                await table.DeleteMembershipTableEntriesAsync("cluster", cancellationToken);
                break;
            case nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync):
                await table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch, cancellationToken);
                break;
            case nameof(IMembershipTable.ReadRowAsync):
                return await table.ReadRowAsync(provider.Entry.SiloAddress, cancellationToken);
            case nameof(IMembershipTable.ReadAllAsync):
                return await table.ReadAllAsync(cancellationToken);
            case nameof(IMembershipTable.InsertRowAsync):
                return await table.InsertRowAsync(provider.Entry, provider.Data.Version, cancellationToken);
            case nameof(IMembershipTable.UpdateRowAsync):
                return await table.UpdateRowAsync(provider.Entry, "etag", provider.Data.Version, cancellationToken);
            case nameof(IMembershipTable.UpdateIAmAliveAsync):
                await table.UpdateIAmAliveAsync(provider.Entry, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return null;
    }

    private static object[] ExpectedArguments(LegacyProvider provider, string operation) => operation switch
    {
        nameof(IMembershipTable.InitializeMembershipTableAsync) => [true],
        nameof(IMembershipTable.DeleteMembershipTableEntriesAsync) => ["cluster"],
        nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync) => [DateTimeOffset.UnixEpoch],
        nameof(IMembershipTable.ReadRowAsync) => [provider.Entry.SiloAddress],
        nameof(IMembershipTable.ReadAllAsync) => [],
        nameof(IMembershipTable.InsertRowAsync) => [provider.Entry, provider.Data.Version],
        nameof(IMembershipTable.UpdateRowAsync) => [provider.Entry, "etag", provider.Data.Version],
        nameof(IMembershipTable.UpdateIAmAliveAsync) => [provider.Entry],
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static Type GetLegacyInvokerType(string legacyId) =>
        typeof(IMembershipTable).Assembly.GetType($"OrleansCodeGen.Orleans.Invokable_IMembershipTable_GrainReference_{legacyId}", throwOnError: true)!;

    private static Type GetCurrentInvokerType(string operation)
    {
        var method = Assert.Single(typeof(IMembershipTable).GetMethods(), method => method.Name == operation);
        var legacyId = Assert.Single(method.GetCustomAttributes<AliasAttribute>()).Alias;
        return Assert.Single(typeof(IMembershipTable).Assembly.GetTypes(),
            type => typeof(IInvokable).IsAssignableFrom(type)
                && type.GetCustomAttributes<CompoundTypeAliasAttribute>().Any(
                    alias => alias.Components.SequenceEqual(new object[] { "inv", typeof(GrainReference), typeof(IMembershipTable), legacyId })));
    }

    private static string GetLegacyAlias(string legacyId) =>
        $"(\"inv\",[GrainRef],[Orleans.IMembershipTable,Orleans.Core],\"{legacyId}\")";

    private static ServiceProvider CreateSerializerServices(bool useLegacyMetadata = false)
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(IMembershipTable).Assembly));
        if (useLegacyMetadata)
        {
            var operations = typeof(IMembershipTable).GetMethods()
                .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal))
                .Select(method =>
                {
                    var legacyId = Assert.Single(method.GetCustomAttributes<AliasAttribute>()).Alias;
                    return (Id: legacyId, Legacy: GetLegacyInvokerType(legacyId), Current: GetCurrentInvokerType(method.Name));
                }).ToArray();
            services.AddSingleton<ITypeConverter>(new LegacyInvokerTypeFormatter(
                operations.ToDictionary(operation => operation.Legacy, operation => GetLegacyAlias(operation.Id))));
            services.PostConfigure<TypeManifestOptions>(options =>
            {
                var aliases = options.CompoundTypeAliases.Add("inv").Add(typeof(GrainReference)).Add(typeof(IMembershipTable));
                foreach (var operation in operations)
                {
                    // Clear the current alias owner before installing the baseline owner.
                    aliases.Add(operation.Id);
                    aliases.Add(operation.Id, operation.Legacy);
                    aliases.Add(operation.Current.Name[(operation.Current.Name.LastIndexOf('_') + 1)..]);
                }

                var currentTypes = operations.Select(operation => operation.Current).ToHashSet();
                options.Serializers.RemoveWhere(RegistersCurrentInvoker);
                options.FieldCodecs.RemoveWhere(RegistersCurrentInvoker);
                options.Copiers.RemoveWhere(RegistersCurrentInvoker);
                options.Activators.RemoveWhere(RegistersCurrentInvoker);

                bool RegistersCurrentInvoker(Type implementation) =>
                    implementation.GetInterfaces().Any(type => type.IsGenericType && type.GenericTypeArguments.Any(currentTypes.Contains));
            });
        }

        var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<IOptions<TypeManifestOptions>>().Value.AllowAllTypes);
        return provider;
    }

    private static async Task<IInvokable> CaptureProxyCall(ServiceProvider services, string operation, object[] arguments)
    {
        var runtime = new CapturingRuntime();
        var shared = new GrainReferenceShared(
            GrainType.Create("membership-wire-test"),
            GrainInterfaceType.Create("membership-wire-test"),
            interfaceVersion: 0,
            runtime,
            InvokeMethodOptions.None,
            services.GetRequiredService<CodecProvider>(),
            services.GetRequiredService<CopyContextPool>(),
            services);
        var proxyType = Assert.Single(services.GetRequiredService<IOptions<TypeManifestOptions>>().Value.InterfaceProxies,
            type => type.Assembly == typeof(IMembershipTable).Assembly && typeof(IMembershipTableSystemTarget).IsAssignableFrom(type));
        var proxy = Activator.CreateInstance(proxyType, shared, IdSpan.Create("membership"));

        var method = Assert.Single(typeof(IMembershipTable).GetMethods(), method => method.Name == operation);
        await Assert.IsAssignableFrom<Task>(method.Invoke(proxy, arguments));

        return Assert.Single(runtime.Calls);
    }

    private static void AssertArguments(object[] expected, object?[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            switch (expected[i])
            {
                case MembershipEntry entry:
                    Assert.Equal(entry.SiloAddress, Assert.IsType<MembershipEntry>(actual[i]).SiloAddress);
                    break;
                case TableVersion version:
                    var actualVersion = Assert.IsType<TableVersion>(actual[i]);
                    Assert.Equal(version.Version, actualVersion.Version);
                    Assert.Equal(version.VersionEtag, actualVersion.VersionEtag);
                    break;
                default:
                    Assert.Equal(expected[i], actual[i]);
                    break;
            }
        }
    }

    // Baseline metadata emitted aliases for these retained types. Parsing still uses the real Orleans resolver.
    private sealed class LegacyInvokerTypeFormatter(IReadOnlyDictionary<Type, string> aliases) : ITypeConverter
    {
        public bool TryFormat(Type type, out string formatted)
        {
            if (aliases.TryGetValue(type, out var alias))
            {
                formatted = alias;
                return true;
            }

            formatted = null!;
            return false;
        }

        public bool TryParse(string formatted, out Type type)
        {
            type = null!;
            return false;
        }
    }

    private sealed class MembershipTargetHolder(IMembershipTable target) : ITargetHolder
    {
        public object GetTarget() => target;
        public object? GetComponent(Type componentType) => null;
    }

    private sealed class CapturingRuntime : IGrainReferenceRuntime
    {
        public List<IInvokable> Calls { get; } = [];

        public ValueTask<T?> InvokeMethodAsync<T>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            Calls.Add(request);
            return default;
        }

        public ValueTask InvokeMethodAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            Calls.Add(request);
            return default;
        }

        public void InvokeMethod(GrainReference reference, IInvokable request, InvokeMethodOptions options) => Calls.Add(request);
        public object Cast(IAddressable grain, Type interfaceType) => grain;
    }

    private class LegacyProvider : IMembershipTable
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Method, object[] Arguments)> Calls { get; } = [];
        public MembershipEntry Entry { get; } = new() { SiloAddress = SiloAddress.FromParsableString("127.0.0.1:100@100") };
        public MembershipTableData Data { get; } = new(new TableVersion(1, "version"));

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => Record(nameof(InitializeMembershipTable), tryInitTableVersion);
        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => Record(nameof(DeleteMembershipTableEntries), clusterId);
        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => Record(nameof(CleanupDefunctSiloEntries), beforeDate);
        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => Record(nameof(InsertRow), entry, tableVersion);
        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => Record(nameof(UpdateRow), entry, etag, tableVersion);
        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => Record(nameof(UpdateIAmAlive), entry);

        [Obsolete("Use ReadRowAsync instead.")]
        public async Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            await Record(nameof(ReadRow), key);
            return Data;
        }

        [Obsolete("Use ReadAllAsync instead.")]
        public async Task<MembershipTableData> ReadAll()
        {
            await Record(nameof(ReadAll));
            return Data;
        }

        private Task<bool> Record(string method, params object[] arguments)
        {
            Calls.Add((method, arguments));
            return Completion.Task;
        }
    }

    private sealed class NativeProvider : LegacyProvider, IMembershipTable
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedToken = cancellationToken;
            return Task.FromResult(Data);
        }
    }
}
