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
            var operations = WireOperations
                .Select(row =>
                {
                    var (methodName, legacyId) = row.Data;
                    return (Id: legacyId, Legacy: GetLegacyInvokerType(legacyId), Current: GetCurrentInvokerType(methodName));
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

    public static TheoryData<string, string> RichWireOperations { get; } = new()
    {
        { nameof(IMembershipTable.InsertRowWithResultAsync), "InsertRowWithResult" },
        { nameof(IMembershipTable.UpdateRowWithResultAsync), "UpdateRowWithResult" },
    };

    [Theory]
    [MemberData(nameof(RichWireOperations))]
    public void WriteResultMethods_HaveAdditiveAliasesAndCancellationLast(string operation, string alias)
    {
        var isInsert = operation == nameof(IMembershipTable.InsertRowWithResultAsync);
        Type[] applicationTypes = isInsert
            ? [typeof(MembershipEntry), typeof(TableVersion)]
            : [typeof(MembershipEntry), typeof(string), typeof(TableVersion)];
        string[] applicationNames = isInsert ? ["entry", "tableVersion"] : ["entry", "etag", "tableVersion"];
        var method = Assert.Single(typeof(IMembershipTable).GetMethods(), candidate => candidate.Name == operation);
        var parameters = method.GetParameters();

        Assert.Equal(typeof(Task<MembershipTableWriteResult>), method.ReturnType);
        Assert.Equal(applicationTypes.Append(typeof(CancellationToken)), parameters.Select(parameter => parameter.ParameterType));
        Assert.Equal(applicationNames.Append("cancellationToken"), parameters.Select(parameter => parameter.Name));
        Assert.All(parameters[..^1], parameter => Assert.False(parameter.IsOptional));
        Assert.True(parameters[^1].IsOptional);
        Assert.True(parameters[^1].HasDefaultValue);
        Assert.Null(parameters[^1].DefaultValue); // Reflection represents default(CancellationToken) as null.
        Assert.Equal(alias, Assert.Single(method.GetCustomAttributes<AliasAttribute>()).Alias);
        Assert.NotEqual("FEF3AC5A", alias);
        Assert.NotEqual("E06D3DBC", alias);
        Assert.NotEqual("D851FB33", alias);

        var boolMethodName = isInsert ? nameof(IMembershipTable.InsertRowAsync) : nameof(IMembershipTable.UpdateRowAsync);
        var boolMethod = Assert.Single(typeof(IMembershipTable).GetMethods(), candidate => candidate.Name == boolMethodName);
        Assert.Equal(typeof(Task<bool>), boolMethod.ReturnType);
        Assert.Equal(applicationTypes.Append(typeof(CancellationToken)), boolMethod.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(isInsert ? "FEF3AC5A" : "E06D3DBC", Assert.Single(boolMethod.GetCustomAttributes<AliasAttribute>()).Alias);
        var tokenlessMethod = typeof(IMembershipTable).GetMethod(boolMethodName[..^"Async".Length], applicationTypes);
        Assert.NotNull(tokenlessMethod);
        Assert.Equal(typeof(Task<bool>), tokenlessMethod.ReturnType);
    }

    [Theory]
    [MemberData(nameof(RichWireOperations))]
    public async Task GeneratedRichProxyCalls_UseAdditiveWireIdentityAndPayload(string operation, string alias)
    {
        using var services = CreateSerializerServices();
        var serializer = services.GetRequiredService<Serializer>();
        var converter = services.GetRequiredService<TypeConverter>();
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11112@83"),
            Status = SiloStatus.Active,
            HostName = "rich-wire-host",
            SiloName = "rich-wire-silo",
            StartTime = DateTime.UnixEpoch.AddHours(2),
            IAmAliveTime = DateTime.UnixEpoch.AddHours(3),
        };
        var version = new TableVersion(42, "table/expected:\"opaque\"");
        object[] arguments = operation == nameof(IMembershipTable.InsertRowWithResultAsync)
            ? [entry, version]
            : [entry, "row/expected:\u03BB", version];
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        using var firstRequest = await CaptureProxyCall(services, operation, [.. arguments, firstCancellation.Token]);
        using var secondRequest = await CaptureProxyCall(services, operation, [.. arguments, secondCancellation.Token]);
        var requestType = GetCurrentInvokerType(operation);
        var wireIdentity = $"(\"inv\",[GrainRef],[Orleans.IMembershipTable,Orleans.Core],\"{alias}\")";

        Assert.Equal(wireIdentity, converter.Format(requestType));
        Assert.Equal(requestType, converter.Parse(wireIdentity));
        Assert.NotEqual(GetCurrentInvokerType(nameof(IMembershipTable.InsertRowAsync)), requestType);
        Assert.NotEqual(GetCurrentInvokerType(nameof(IMembershipTable.UpdateRowAsync)), requestType);
        Assert.NotEqual(firstCancellation.Token, secondCancellation.Token);
        AssertRequest(firstRequest, firstCancellation.Token);
        AssertRequest(secondRequest, secondCancellation.Token);

        var bytes = serializer.SerializeToArray<object>(firstRequest);
        Assert.Equal(bytes, serializer.SerializeToArray<object>(secondRequest));
        firstCancellation.Cancel();
        Assert.Equal(bytes, serializer.SerializeToArray<object>(firstRequest));
        using var received = Assert.IsAssignableFrom<IInvokable>(serializer.Deserialize<object>(bytes));
        AssertRequest(received, CancellationToken.None);

        // This is a new receiver returning its raw commit result, not a simulated historical rich receiver.
        var committedVersion = new TableVersion(42, "table/committed:\"different\"");
        var receipt = new MembershipTableWriteReceipt(committedVersion, "row/committed:\u03A9");
        var target = new RichWriteProvider(new MembershipTableWriteResult(true, receipt));
        target.Completion.SetResult(false);
        received.SetTarget(new MembershipTargetHolder(target));
        // SetTarget creates receiver-local cancellation state; no caller token crossed the wire.
        var receiverToken = received.GetCancellationToken();
        Assert.True(receiverToken.CanBeCanceled);
        Assert.False(receiverToken.IsCancellationRequested);
        Assert.NotEqual(firstCancellation.Token, receiverToken);
        Assert.NotEqual(secondCancellation.Token, receiverToken);

        using var response = await received.Invoke();

        Assert.Null(response.Exception);
        var result = Assert.IsType<MembershipTableWriteResult>(response.Result);
        Assert.True(result.Succeeded);
        var returnedReceipt = Assert.IsType<MembershipTableWriteReceipt>(result.Receipt);
        Assert.Same(receipt, returnedReceipt);
        Assert.Same(committedVersion, returnedReceipt.Version);
        Assert.Equal(42, returnedReceipt.Version.Version);
        Assert.Equal("table/committed:\"different\"", returnedReceipt.Version.VersionEtag);
        Assert.Equal("row/committed:\u03A9", returnedReceipt.RowETag);
        var call = Assert.Single(target.RichCalls);
        Assert.Equal(operation, call.Method);
        Assert.Equal(receiverToken, call.Token);
        AssertRichArguments(call.Arguments);
        Assert.Empty(target.Calls);

        void AssertRequest(IInvokable request, CancellationToken token)
        {
            Assert.Equal(requestType, request.GetType());
            Assert.Equal(operation, request.GetMethodName());
            Assert.Equal(typeof(IMembershipTable).GetMethod(operation), request.GetMethod());
            Assert.Equal(typeof(IMembershipTable), request.GetInterfaceType());
            Assert.True(request.IsCancellable);
            Assert.Equal(arguments.Length + 1, request.GetArgumentCount());
            Assert.Equal(token, request.GetCancellationToken());
            Assert.Equal(token, Assert.IsType<CancellationToken>(request.GetArgument(arguments.Length)));
            AssertRichArguments(Enumerable.Range(0, arguments.Length).Select(request.GetArgument).ToArray());
        }

        void AssertRichArguments(object?[] actual)
        {
            AssertArguments(arguments, actual);
            var actualEntry = Assert.IsType<MembershipEntry>(actual[0]);
            Assert.Equal(SiloStatus.Active, actualEntry.Status);
            Assert.Equal("rich-wire-host", actualEntry.HostName);
            Assert.Equal("rich-wire-silo", actualEntry.SiloName);
            Assert.Equal(DateTime.UnixEpoch.AddHours(2), actualEntry.StartTime);
            Assert.Equal(DateTime.UnixEpoch.AddHours(3), actualEntry.IAmAliveTime);
        }
    }

    private sealed class RichWriteProvider(MembershipTableWriteResult result) : LegacyProvider, IMembershipTable
    {
        public List<(string Method, object[] Arguments, CancellationToken Token)> RichCalls { get; } = [];

        public Task<MembershipTableWriteResult> InsertRowWithResultAsync(
            MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            RichCalls.Add((nameof(InsertRowWithResultAsync), [entry, tableVersion], cancellationToken));
            return Task.FromResult(result);
        }

        public Task<MembershipTableWriteResult> UpdateRowWithResultAsync(
            MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            RichCalls.Add((nameof(UpdateRowWithResultAsync), [entry, etag, tableVersion], cancellationToken));
            return Task.FromResult(result);
        }
    }
}
