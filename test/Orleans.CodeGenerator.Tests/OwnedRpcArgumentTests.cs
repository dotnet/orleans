using System.Buffers;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.CodeGenerator.Tests;

public sealed class OwnedRpcArgumentTests
{
    [Fact]
    public async Task Attribute_IsPublicParameterOnly_AndResolvedFromMetadata()
    {
        var attribute = typeof(DisposeOnCompletionAttribute);
        Assert.True(attribute.IsPublic);
        Assert.Equal(AttributeTargets.Parameter, attribute.GetCustomAttribute<AttributeUsageAttribute>()!.ValidOn);
        using var fixture = await GeneratedFixture.Create();
        var symbol = fixture.Input.GetTypeByMetadataName("Orleans.DisposeOnCompletionAttribute");
        Assert.NotNull(symbol);
        Assert.Equal(typeof(GenerateSerializerAttribute).Assembly.GetName().Name, symbol!.ContainingAssembly.Name);
        var method = fixture.Input.GetTypeByMetadataName("OwnershipFixtures.IOwnedCalls")!
            .GetMembers("Owned").OfType<IMethodSymbol>().Single();
        Assert.True(SymbolEqualityComparer.Default.Equals(symbol, Assert.Single(method.Parameters[0].GetAttributes()).AttributeClass));
        Assert.Empty(method.Parameters[1].GetAttributes());

        var request = fixture.Request("Owned");
        var parameter = request.GetMethod().GetParameters()[0];
        Assert.IsType<DisposeOnCompletionAttribute>(Assert.Single(parameter.GetCustomAttributes()));
        Assert.Equal("owned", parameter.Name);
        Assert.Equal(3, request.GetArgumentCount());
        request.Dispose();
    }

    [Fact]
    public async Task AnnotatedArguments_DisposeBeforeResetAndCtsCleanup_UnannotatedUnchanged()
    {
        using var fixture = await GeneratedFixture.Create();
        var declaration = fixture.Declaration("Owned");
        var dispose = Assert.Single(declaration.Members.OfType<MethodDeclarationSyntax>(), method => method.Identifier.ValueText == "Dispose");
        var lifetime = Assert.IsType<TryStatementSyntax>(Assert.Single(dispose.Body!.Statements));
        Assert.NotNull(lifetime.Finally);
        var statements = dispose.Body.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Select(statement => statement.ToString()).ToArray();
        var owned = Array.FindIndex(statements, statement => statement.Contains("CompleteArgumentResources()", StringComparison.Ordinal));
        var borrowed = Array.FindIndex(statements, statement => statement == "arg1 = default;");
        var ctsDispose = Array.FindIndex(statements, statement => statement == "_cts?.Dispose();");
        var ctsReset = Array.FindIndex(statements, statement => statement == "_cts = default;");
        Assert.True(owned >= 0, dispose.ToString());
        Assert.True(owned < borrowed && borrowed < ctsDispose && ctsDispose < ctsReset, dispose.ToString());
        Assert.DoesNotContain(statements, statement => statement.Contains("DisposeOwnedArgument(ref arg1)", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, statement => statement == "arg0 = default;");
        Assert.Contains("arg2 = default;", statements);
        Assert.Contains("public override bool IsCancellable => true;", declaration.ToString(), StringComparison.Ordinal);
        var cleanup = Assert.Single(declaration.Members.OfType<MethodDeclarationSyntax>(), method => method.Identifier.ValueText == "DisposeOwnedArguments");
        Assert.Contains("DisposeOwnedArgument(ref arg0)", cleanup.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeOwnedArgument(ref arg1)", cleanup.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedInvokable_DisposesOwnedArgumentExactlyOnce_AndResetsAllArguments()
    {
        using var fixture = await GeneratedFixture.Create();
        var request = fixture.Request("Owned");
        var owned = fixture.Envelope(71);
        var borrowed = fixture.Envelope(93);
        using var cts = new CancellationTokenSource();
        request.SetArgument(0, owned);
        request.SetArgument(1, borrowed);
        request.SetArgument(2, cts.Token);
        fixture.SetCts(request, cts);
        var observations = 0;
        fixture.Set(owned, "OnDispose", (Action)(() =>
        {
            observations++;
            Assert.Same(owned, request.GetArgument(0)); // Dispose must precede field clearing.
            Assert.Same(borrowed, request.GetArgument(1));
            Assert.Equal(71, fixture.Get<int>(owned, "Marker"));
            Assert.Equal(cts.Token, request.GetCancellationToken()); // CTS has not been disposed yet.
        }));

        request.Dispose();
        request.Dispose();

        Assert.Equal(1, observations);
        Assert.Equal(1, fixture.Get<int>(owned, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(borrowed, "Disposes"));
        Assert.Equal(93, fixture.Get<int>(borrowed, "Marker"));
        Assert.Null(request.GetArgument(0));
        Assert.Null(request.GetArgument(1));
        Assert.Equal(default(CancellationToken), request.GetArgument(2));
        Assert.Equal(CancellationToken.None, request.GetCancellationToken());
        Assert.Null(fixture.GetCts(request));
        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
    }

    [Theory]
    [InlineData("Owned")]
    [InlineData("StructOwned")]
    [InlineData("ArcOwned")]
    public async Task DefaultAndEmptyArguments_DisposeSafely(string method)
    {
        using var fixture = await GeneratedFixture.Create();
        var request = fixture.Request(method);
        request.Dispose();
        request.Dispose();
        if (method == "Owned") Assert.Null(request.GetArgument(0));
        else if (method == "ArcOwned")
        {
            var empty = Assert.IsType<ArcBuffer>(request.GetArgument(0));
            Assert.Equal(0, empty.Length);
            Assert.Equal(Array.Empty<byte>(), empty.ToArray());
        }
        else
        {
            Assert.Equal(0, fixture.Get<int>(request.GetArgument(0)!, "Marker"));
            Assert.Equal(0, fixture.ValueEnvelopeType.GetField("Disposes")!.GetValue(null));
        }
        if (method != "ArcOwned")
        {
            Assert.Null(fixture.GetCts(request));
            Assert.Equal(CancellationToken.None, request.GetCancellationToken());
        }
    }

    [Fact]
    public async Task ValueEnvelope_ExplicitDisposeSeesValueBeforeReset_AndCleansCancellation()
    {
        using var fixture = await GeneratedFixture.Create();
        var request = fixture.Request("StructOwned");
        var value = Activator.CreateInstance(fixture.ValueEnvelopeType)!;
        fixture.Set(value, "Marker", 41);
        using var cts = new CancellationTokenSource();
        request.SetArgument(0, value);
        request.SetArgument(1, cts.Token);
        fixture.SetCts(request, cts);
        var observations = 0;
        fixture.ValueEnvelopeType.GetField("OnDispose")!.SetValue(null, (Action<int>)(marker =>
        {
            observations++;
            Assert.Equal(41, marker);
            Assert.Equal(41, fixture.Get<int>(request.GetArgument(0)!, "Marker"));
            Assert.Equal(cts.Token, request.GetCancellationToken());
        }));
        try
        {
            request.Dispose();
            request.Dispose();
            Assert.Equal(1, observations);
            Assert.Equal(1, fixture.ValueEnvelopeType.GetField("Disposes")!.GetValue(null));
            Assert.Equal(0, fixture.Get<int>(request.GetArgument(0)!, "Marker"));
            Assert.Equal(CancellationToken.None, request.GetCancellationToken());
            Assert.Null(fixture.GetCts(request));
            Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
        }
        finally
        {
            fixture.ValueEnvelopeType.GetField("OnDispose")!.SetValue(null, null);
        }
    }

    [Theory]
    [InlineData("GenericOwned", false)]
    [InlineData("GenericOwned", true)]
    [InlineData("InterfaceOwned", false)]
    [InlineData("InterfaceOwned", true)]
    public async Task GenericInterfaceAndMethod_OwnedValueArgumentCompilesAndDisposes(string method, bool valueType)
    {
        using var fixture = await GeneratedFixture.Create();
        var argumentType = valueType ? fixture.ValueEnvelopeType : fixture.EnvelopeType;
        var request = fixture.Request(method, argumentType);
        var argument = valueType ? Activator.CreateInstance(argumentType)! : fixture.Envelope(52);
        if (valueType) fixture.Set(argument, "Marker", 52);
        request.SetArgument(0, argument);
        using var cts = new CancellationTokenSource();
        request.SetArgument(1, cts.Token);
        fixture.SetCts(request, cts);
        Assert.Equal(2, request.GetArgumentCount());
        Assert.True(request.IsCancellable);
        Assert.Equal(cts.Token, request.GetCancellationToken());

        request.Dispose();
        request.Dispose();

        if (valueType)
        {
            Assert.Equal(1, argumentType.GetField("Disposes")!.GetValue(null));
            Assert.Equal(0, fixture.Get<int>(request.GetArgument(0)!, "Marker"));
        }
        else
        {
            Assert.Equal(1, fixture.Get<int>(argument, "Disposes"));
            Assert.Null(request.GetArgument(0));
        }
        Assert.Null(fixture.GetCts(request));
        Assert.Equal(CancellationToken.None, request.GetCancellationToken());
        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
    }

    [Fact]
    public async Task ThrowingOwnedDispose_PreservesOriginalError_AndFinallyClearsOwnedField()
    {
        using var fixture = await GeneratedFixture.Create();
        var request = fixture.Request("Owned");
        var owned = fixture.Envelope(17);
        var borrowed = fixture.Envelope(23);
        var expected = new InvalidOperationException("original release error");
        fixture.Set(owned, "Failure", expected);
        request.SetArgument(0, owned);
        request.SetArgument(1, borrowed);
        using var cts = new CancellationTokenSource();
        request.SetArgument(2, cts.Token);
        fixture.SetCts(request, cts);

        Assert.Same(expected, Record.Exception(request.Dispose));

        Assert.Equal(1, fixture.Get<int>(owned, "Disposes"));
        Assert.Null(request.GetArgument(0)); // The helper's finally must run despite release failure.
        Assert.Null(request.GetArgument(1)); // Other fields and CTS still clean up after release failure.
        Assert.Equal(0, fixture.Get<int>(borrowed, "Disposes"));
        Assert.Null(fixture.GetCts(request));
        Assert.Equal(CancellationToken.None, request.GetCancellationToken());
        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
        request.Dispose();
        Assert.Equal(1, fixture.Get<int>(owned, "Disposes"));
        Assert.Null(request.GetArgument(1));
    }

    [Fact]
    public async Task MultipleOwnedFailures_ReleaseEveryArgumentAndCts_PreserveFirstError()
    {
        using var fixture = await GeneratedFixture.Create();
        var request = fixture.Request("TwoOwned");
        var first = fixture.Envelope(101);
        var second = fixture.Envelope(202);
        var expected = new InvalidOperationException("first release error");
        fixture.Set(first, "Failure", expected);
        fixture.Set(second, "Failure", new InvalidOperationException("second release error"));
        request.SetArgument(0, first);
        request.SetArgument(1, second);
        using var cts = new CancellationTokenSource();
        request.SetArgument(2, cts.Token);
        fixture.SetCts(request, cts);

        Assert.Same(expected, Record.Exception(request.Dispose));

        Assert.Equal(1, fixture.Get<int>(first, "Disposes"));
        Assert.Equal(1, fixture.Get<int>(second, "Disposes"));
        Assert.Null(request.GetArgument(0));
        Assert.Null(request.GetArgument(1));
        Assert.Null(fixture.GetCts(request));
        Assert.Equal(CancellationToken.None, request.GetCancellationToken());
        Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
        request.Dispose();
        Assert.Equal(1, fixture.Get<int>(first, "Disposes"));
        Assert.Equal(1, fixture.Get<int>(second, "Disposes"));
    }

    [Fact]
    public async Task OnlyAnnotatedRequestsImplementArgumentOwnership_UnannotatedRemainBorrowed()
    {
        using var fixture = await GeneratedFixture.Create();
        var ownedRequest = fixture.Request("Owned");
        var borrowedRequest = fixture.Request("Unowned");
        Assert.IsAssignableFrom<IInvokableArgumentOwner>(ownedRequest);
        Assert.IsNotAssignableFrom<IInvokableArgumentOwner>(borrowedRequest);
        Assert.DoesNotContain(fixture.Declaration("Unowned").Members.OfType<FieldDeclarationSyntax>(),
            field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "_ownedArgumentState"));
        Assert.DoesNotContain("DisposeOwnedArgument", fixture.Declaration("Unowned").ToString(), StringComparison.Ordinal);
        var borrowed = fixture.Envelope(67);
        borrowedRequest.SetArgument(0, borrowed);

        borrowedRequest.Dispose();

        Assert.Equal(0, fixture.Get<int>(borrowed, "Disposes"));
        Assert.Equal(67, fixture.Get<int>(borrowed, "Marker"));
        Assert.Null(borrowedRequest.GetArgument(0));
        ownedRequest.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveUses_DelayOwnedDisposalUntilLastRelease_RepeatedTerminalSignalsAreNoOps(bool fullDispose)
    {
        using var fixture = await GeneratedFixture.Create();
        var request = fixture.Request("Owned");
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        var argument = fixture.Envelope(79);
        request.SetArgument(0, argument);
        Assert.True(owner.TryRetainArgumentResources());
        owner.ReleaseArgumentResources(); // Releasing a use must not complete the initial owner.
        Assert.Equal(0, fixture.Get<int>(argument, "Disposes"));
        Assert.Same(argument, request.GetArgument(0));
        Assert.True(owner.TryRetainArgumentResources());
        Assert.True(owner.TryRetainArgumentResources());

        if (fullDispose) request.Dispose();
        else owner.CompleteArgumentResources();
        owner.CompleteArgumentResources();
        request.Dispose();
        Assert.False(owner.TryRetainArgumentResources());
        Assert.Equal(0, fixture.Get<int>(argument, "Disposes"));
        Assert.Same(argument, request.GetArgument(0));
        Assert.Equal(79, fixture.Get<int>(request.GetArgument(0)!, "Marker"));

        owner.ReleaseArgumentResources();
        Assert.Equal(0, fixture.Get<int>(argument, "Disposes"));
        Assert.Same(argument, request.GetArgument(0));
        owner.ReleaseArgumentResources();
        Assert.Equal(1, fixture.Get<int>(argument, "Disposes"));
        Assert.Null(request.GetArgument(0));
        owner.CompleteArgumentResources();
        request.Dispose();
        Assert.Equal(1, fixture.Get<int>(argument, "Disposes"));
        Assert.False(owner.TryRetainArgumentResources());
    }

    [Fact]
    public async Task ActiveArcUse_DisposePreservesBytesAndPins_UntilFinalRelease()
    {
        using var fixture = await GeneratedFixture.Create();
        var services = new ServiceCollection();
        services.AddSerializer();
        using var provider = services.BuildServiceProvider();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 13, 21, 34, 55, 89 });
        using var original = writer.PeekSlice(writer.Length);
        var request = fixture.Request("ArcOwned");
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        request.SetArgument(0, provider.GetRequiredService<DeepCopier<ArcBuffer>>().Copy(original));
        Assert.True(owner.TryRetainArgumentResources());
        Assert.Equal(3, References(original.First));

        request.Dispose();
        owner.CompleteArgumentResources();

        Assert.False(owner.TryRetainArgumentResources());
        Assert.Equal(3, References(original.First));
        Assert.Equal(new byte[] { 13, 21, 34, 55, 89 }, Assert.IsType<ArcBuffer>(request.GetArgument(0)).ToArray());
        Assert.Equal(new byte[] { 13, 21, 34, 55, 89 }, original.ToArray());
        owner.ReleaseArgumentResources();
        Assert.Equal(2, References(original.First));
        var cleared = Assert.IsType<ArcBuffer>(request.GetArgument(0));
        Assert.Equal(0, cleared.Length);
        Assert.Null(cleared.First);
        request.Dispose();
        owner.CompleteArgumentResources();
        Assert.Equal(2, References(original.First));
        Assert.Equal(new byte[] { 13, 21, 34, 55, 89 }, original.ToArray());
    }

    [Fact]
    public async Task ConcurrentRetainAndCompletion_LastHeldUseReleasesExactlyOnce()
    {
        using var fixture = await GeneratedFixture.Create();
        var request = fixture.Request("Owned");
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        var argument = fixture.Envelope(97);
        request.SetArgument(0, argument);
        Assert.True(owner.TryRetainArgumentResources()); // Hold one use across every concurrent terminal signal.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return owner.TryRetainArgumentResources();
        })).ToArray();
        var completions = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            owner.CompleteArgumentResources();
        })).ToArray();
        start.SetResult();
        var retained = await Task.WhenAll(attempts);
        await Task.WhenAll(completions);

        Assert.False(owner.TryRetainArgumentResources());
        Assert.Equal(0, fixture.Get<int>(argument, "Disposes"));
        Assert.Same(argument, request.GetArgument(0));
        // Every successful racing retain has exactly one matching release; no assumed
        // scheduler outcome, count range, timeout, polling, or timing-sensitive assertion.
        foreach (var success in retained)
        {
            if (success) owner.ReleaseArgumentResources();
        }
        Assert.Equal(0, fixture.Get<int>(argument, "Disposes"));
        Assert.Same(argument, request.GetArgument(0));
        owner.ReleaseArgumentResources();
        Assert.Equal(1, fixture.Get<int>(argument, "Disposes"));
        Assert.Null(request.GetArgument(0));
        owner.CompleteArgumentResources();
        request.Dispose();
        Assert.Equal(1, fixture.Get<int>(argument, "Disposes"));
    }

    [Fact]
    public async Task GeneratedInvokable_DisposingArcCopy_LeavesOriginalPinnedAndReadable()
    {
        using var fixture = await GeneratedFixture.Create();
        var services = new ServiceCollection();
        services.AddSerializer();
        using var provider = services.BuildServiceProvider();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 0, 17, 255, 128, 42 });
        var original = writer.PeekSlice(writer.Length);
        var page = original.First;
        try
        {
            Assert.Equal(2, References(page));
            var copy = provider.GetRequiredService<DeepCopier<ArcBuffer>>().Copy(original);
            var request = fixture.Request("ArcOwned");
            request.SetArgument(0, copy); // Move the copier's owner into the generated request.
            Assert.Equal(3, References(page));
            Assert.Equal(new byte[] { 0, 17, 255, 128, 42 }, Assert.IsType<ArcBuffer>(request.GetArgument(0)).ToArray());
            request.Dispose();
            request.Dispose();
            Assert.Equal(2, References(page));
            var cleared = Assert.IsType<ArcBuffer>(request.GetArgument(0));
            Assert.Equal(0, cleared.Length);
            Assert.Null(cleared.First);
            Assert.Equal(new byte[] { 0, 17, 255, 128, 42 }, original.ToArray());
            Assert.Equal(5, writer.Length);
        }
        finally
        {
            original.Dispose();
        }
        Assert.Equal(1, References(page));
    }

    [Fact]
    public async Task GeneratedCopierFailure_ReleasesEarlierArcCopy_PreservesSourceRequestAndCallerPins()
    {
        using var fixture = await GeneratedFixture.Create();
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(fixture.Assembly));
        var arcCopier = new ObservingArcCopier();
        services.Configure<TypeManifestOptions>(options =>
            options.AddSerializer<ArcBuffer>(_ => new ArcBufferCodec(), _ => arcCopier));
        using var provider = services.BuildServiceProvider();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 6, 10, 15, 21 });
        using var caller = writer.PeekSlice(writer.Length);
        using var invalidWriter = new ArcBufferWriter();
        invalidWriter.Write(new byte[] { 99 });
        var disposed = invalidWriter.PeekSlice(1);
        disposed.Dispose();
        var request = fixture.Request("TwoArc");
        request.SetArgument(0, provider.GetRequiredService<DeepCopier<ArcBuffer>>().Copy(caller));
        request.SetArgument(1, disposed); // A deliberately invalid second input forces failure after the first copy.
        arcCopier.Copies = 0;
        Assert.Equal(3, References(caller.First));
        var copier = provider.GetRequiredService<DeepCopier>();

        var actual = Assert.Throws<InvalidOperationException>(() => copier.Copy(request));

        Assert.Same(arcCopier.Failure, actual);
        Assert.Equal(2, arcCopier.Copies);
        Assert.Equal(3, References(caller.First)); // No leaked pin from the partially copied generated request.
        Assert.Equal(new byte[] { 6, 10, 15, 21 }, caller.ToArray());
        Assert.Equal(new byte[] { 6, 10, 15, 21 }, Assert.IsType<ArcBuffer>(request.GetArgument(0)).ToArray());
        Assert.Equal(1, References(disposed.First)); // Invalid source has only its writer's pin.
        Assert.Throws<InvalidOperationException>(() => request.Dispose());
        Assert.Equal(2, References(caller.First));
        Assert.Equal(0, Assert.IsType<ArcBuffer>(request.GetArgument(0)).Length);
    }

    [Fact]
    public async Task ProxyLaterArgumentCopyFailure_ReleasesEarlierOwner_LeavesCallerUnchanged()
    {
        using var fixture = await GeneratedFixture.Create();
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(fixture.Assembly));
        var arcCopier = new ObservingArcCopier();
        services.Configure<TypeManifestOptions>(options =>
            options.AddSerializer<ArcBuffer>(_ => new ArcBufferCodec(), _ => arcCopier));
        using var provider = services.BuildServiceProvider();
        var proxy = fixture.Proxy(provider);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 4, 9, 16, 25 });
        using var caller = writer.PeekSlice(writer.Length);
        using var invalidWriter = new ArcBufferWriter();
        invalidWriter.Write(new byte[] { 81 });
        var disposed = invalidWriter.PeekSlice(1);
        disposed.Dispose();

        var actual = Assert.Throws<TargetInvocationException>(() => fixture.Submit(proxy, caller, disposed));

        Assert.Same(arcCopier.Failure, actual.InnerException);
        Assert.Equal(2, arcCopier.Copies);
        Assert.Equal(2, References(caller.First));
        Assert.Equal(1, References(disposed.First));
        Assert.Equal(new byte[] { 4, 9, 16, 25 }, caller.ToArray());
        Assert.Null(fixture.Get<object?>(proxy, "LastSubmitted")); // Submission is not reached.
    }

    [Fact]
    public async Task ProxySynchronousSubmitFailure_CompletesCopiedArgumentsExactlyOnce_PreservesOriginalError()
    {
        using var fixture = await GeneratedFixture.Create();
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(fixture.Assembly));
        var arcCopier = new ObservingArcCopier();
        services.Configure<TypeManifestOptions>(options =>
            options.AddSerializer<ArcBuffer>(_ => new ArcBufferCodec(), _ => arcCopier));
        using var provider = services.BuildServiceProvider();
        var proxy = fixture.Proxy(provider);
        var expected = new InvalidOperationException("synchronous submit error");
        fixture.Set(proxy, "SubmitFailure", expected);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 5, 10, 20, 40 });
        using var caller = writer.PeekSlice(writer.Length);

        var actual = Assert.Throws<TargetInvocationException>(() => fixture.Submit(proxy, caller, caller));

        Assert.Same(expected, actual.InnerException);
        Assert.Equal(2, arcCopier.Copies);
        Assert.Equal(2, References(caller.First));
        Assert.Equal(new byte[] { 5, 10, 20, 40 }, caller.ToArray());
        var request = Assert.IsAssignableFrom<IInvokable>(fixture.Get<object?>(proxy, "LastSubmitted"));
        Assert.Equal(0, Assert.IsType<ArcBuffer>(request.GetArgument(0)).Length);
        Assert.Equal(0, Assert.IsType<ArcBuffer>(request.GetArgument(1)).Length);
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        Assert.False(owner.TryRetainArgumentResources());
        owner.CompleteArgumentResources();
        request.Dispose();
        Assert.Equal(2, References(caller.First));
    }

    [Fact]
    public async Task MalformedLaterWireField_ReleasesEarlierDecodedOwner_LeavesSourceRequestAndCallerAlive()
    {
        using var fixture = await GeneratedFixture.Create();
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(fixture.Assembly));
        var codec = new ObservingArcCodec();
        services.Configure<TypeManifestOptions>(options =>
            options.AddSerializer<ArcBuffer>(_ => codec, _ => new ArcBufferCopier()));
        using var provider = services.BuildServiceProvider();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 7, 14, 28, 56 });
        using var caller = writer.PeekSlice(writer.Length);
        var request = fixture.Request("TwoArc");
        var copier = provider.GetRequiredService<DeepCopier<ArcBuffer>>();
        request.SetArgument(0, copier.Copy(caller));
        request.SetArgument(1, copier.Copy(caller));
        var serializer = provider.GetRequiredService<Serializer<IInvokable>>();
        var wire = serializer.SerializeToArray(request);
        Assert.Equal(4, References(caller.First));
        var malformed = wire[..^2]; // Remove the final byte of the later Arc payload and the object terminator.

        var actual = Assert.Throws<IndexOutOfRangeException>(() => serializer.Deserialize(malformed));

        Assert.Same(codec.Failure, actual);
        Assert.Equal(2, codec.Reads);
        var decodedPage = Assert.Single(codec.DecodedPages); // The first field really acquired ownership.
        Assert.Equal(0, References(decodedPage)); // The generated codec catch completed the partial result.
        Assert.Equal(4, References(caller.First));
        Assert.Equal(new byte[] { 7, 14, 28, 56 }, caller.ToArray());
        Assert.Equal(new byte[] { 7, 14, 28, 56 }, Assert.IsType<ArcBuffer>(request.GetArgument(0)).ToArray());
        Assert.Equal(new byte[] { 7, 14, 28, 56 }, Assert.IsType<ArcBuffer>(request.GetArgument(1)).ToArray());
        request.Dispose();
        Assert.Equal(2, References(caller.First));
    }

    private sealed class ObservingArcCopier : IDeepCopier<ArcBuffer>
    {
        private readonly ArcBufferCopier _inner = new();
        public int Copies { get; set; }
        public Exception? Failure { get; private set; }
        public ArcBuffer DeepCopy(ArcBuffer input, CopyContext context)
        {
            Copies++;
            try { return _inner.DeepCopy(input, context); }
            catch (Exception error)
            {
                Failure = error;
                throw;
            }
        }
    }

    private sealed class ObservingArcCodec : IFieldCodec<ArcBuffer>
    {
        private readonly ArcBufferCodec _inner = new();
        public int Reads { get; private set; }
        public List<ArcBufferPage> DecodedPages { get; } = [];
        public Exception? Failure { get; private set; }
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, ArcBuffer value)
            where TBufferWriter : IBufferWriter<byte> => _inner.WriteField(ref writer, fieldIdDelta, expectedType, value);
        public ArcBuffer ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            Reads++;
            try
            {
                var result = _inner.ReadValue(ref reader, field);
                DecodedPages.Add(result.First);
                return result;
            }
            catch (Exception error)
            {
                Failure = error;
                throw;
            }
        }
    }

    private static int References(ArcBufferPage page) => (int)typeof(ArcBufferPage)
        .GetProperty("ReferenceCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private sealed class GeneratedFixture : IDisposable
    {
        private readonly AssemblyLoadContext _loadContext;
        private readonly Assembly _assembly;
        private readonly ClassDeclarationSyntax[] _declarations;
        public CSharpCompilation Input { get; }
        public Assembly Assembly => _assembly;
        public Type EnvelopeType => _assembly.GetType("OwnershipFixtures.Envelope", throwOnError: true)!;
        public Type ValueEnvelopeType => _assembly.GetType("OwnershipFixtures.ValueEnvelope", throwOnError: true)!;
        private GeneratedFixture(CSharpCompilation input, GeneratorRunResult result, byte[] assembly)
        {
            Input = input;
            _declarations = result.GeneratedSources.SelectMany(source =>
                CSharpSyntaxTree.ParseText(source.SourceText.ToString().TrimStart('\uFEFF')).GetRoot()
                    .DescendantNodes().OfType<ClassDeclarationSyntax>()).ToArray();
            _loadContext = new AssemblyLoadContext("OwnedRpcArguments_" + Guid.NewGuid().ToString("N"), isCollectible: true);
            using var stream = new MemoryStream(assembly);
            _assembly = _loadContext.LoadFromStream(stream);
        }
        public static async Task<GeneratedFixture> Create()
        {
            var input = await TestCompilationHelper.CreateCompilation(Source, "OwnedRpcArguments_" + Guid.NewGuid().ToString("N"));
            AssertNoErrors(input.GetDiagnostics());
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(input, out var output, out var diagnostics);
            AssertNoErrors(diagnostics);
            AssertNoErrors(output.GetDiagnostics());
            var result = Assert.Single(driver.GetRunResult().Results);
            AssertNoErrors(result.Diagnostics);
            using var stream = new MemoryStream();
            var emitted = output.Emit(stream);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return new(input, result, stream.ToArray());
        }
        public ClassDeclarationSyntax Declaration(string method) => _declarations.Single(declaration =>
            declaration.Identifier.ValueText.StartsWith("Invokable_", StringComparison.Ordinal)
            && declaration.Members.OfType<MethodDeclarationSyntax>().Any(member =>
                member.Identifier.ValueText == "GetMethodName"
                && member.ExpressionBody?.Expression is LiteralExpressionSyntax literal
                && literal.Token.ValueText.Split('<')[0] == method));
        public IInvokable Request(string method, params Type[] arguments)
        {
            var name = Declaration(method).Identifier.ValueText;
            var type = _assembly.GetTypes().Single(type => type.Name.Split('`')[0] == name);
            if (type.IsGenericTypeDefinition) type = type.MakeGenericType(arguments);
            var result = Assert.IsAssignableFrom<IInvokable>(Activator.CreateInstance(type, nonPublic: true));
            Assert.Equal(method, result.GetMethodName().Split('<')[0]);
            return result;
        }
        public object Envelope(int marker)
        {
            var result = Activator.CreateInstance(EnvelopeType)!;
            Set(result, "Marker", marker);
            return result;
        }
        public object Proxy(IServiceProvider provider)
        {
            var type = _assembly.GetTypes().Single(type => type.Name == "Proxy_IFaultCalls");
            return Activator.CreateInstance(type, provider.GetRequiredService<ICodecProvider>(), provider.GetRequiredService<CopyContextPool>())!;
        }
        public void Submit(object proxy, ArcBuffer first, ArcBuffer second) =>
            _assembly.GetType("OwnershipFixtures.IFaultCalls")!.GetMethod("Submit")!.Invoke(proxy, [first, second]);
        public void Set(object value, string field, object data) => value.GetType().GetField(field)!.SetValue(value, data);
        public T Get<T>(object value, string field) => (T)value.GetType().GetField(field)!.GetValue(value)!;
        public void SetCts(IInvokable request, CancellationTokenSource cts) =>
            request.GetType().GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(request, cts);
        public object? GetCts(IInvokable request) =>
            request.GetType().GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(request);
        public void Dispose() => _loadContext.Unload();
        private static void AssertNoErrors(IEnumerable<Diagnostic> diagnostics) =>
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Orleans;
        using Orleans.Runtime;
        using Orleans.Serialization.Buffers;
        using Orleans.Serialization.Cloning;
        using Orleans.Serialization.Invocation;
        using Orleans.Serialization.Serializers;

        namespace OwnershipFixtures
        {
            [GenerateSerializer]
            public sealed class Envelope : IDisposable
            {
                [Id(0)] public int Marker;
                public int Disposes;
                public Action OnDispose;
                public Exception Failure;
                void IDisposable.Dispose()
                {
                    Disposes++;
                    OnDispose?.Invoke();
                    if (Failure is not null) throw Failure;
                }
            }

            [GenerateSerializer]
            public struct ValueEnvelope : IDisposable
            {
                [Id(0)] public int Marker;
                public static int Disposes;
                public static Action<int> OnDispose;
                void IDisposable.Dispose()
                {
                    if (Marker == 0) return;
                    Disposes++;
                    OnDispose?.Invoke(Marker);
                }
            }

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface IOwnedCalls : IGrainWithIntegerKey
            {
                ValueTask Owned([DisposeOnCompletion] Envelope owned, Envelope borrowed, CancellationToken cancellationToken);
                ValueTask StructOwned([DisposeOnCompletion] ValueEnvelope owned, CancellationToken cancellationToken);
                ValueTask ArcOwned([DisposeOnCompletion] ArcBuffer owned);
                ValueTask Unowned(Envelope borrowed);
                ValueTask TwoOwned([DisposeOnCompletion] Envelope first, [DisposeOnCompletion] Envelope second, CancellationToken cancellationToken);
                ValueTask TwoArc([DisposeOnCompletion] ArcBuffer first, [DisposeOnCompletion] ArcBuffer second);
                ValueTask GenericOwned<T>([DisposeOnCompletion] T owned, CancellationToken cancellationToken) where T : IDisposable;
            }

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface IOwnedGenericCalls<T> : IGrainWithIntegerKey where T : IDisposable
            {
                ValueTask InterfaceOwned([DisposeOnCompletion] T owned, CancellationToken cancellationToken);
            }

            [DefaultInvokableBaseType(typeof(ValueTask), typeof(Request))]
            public class FailingProxyBase
            {
                protected ICodecProvider CodecProvider { get; }
                protected CopyContextPool CopyContextPool { get; }
                public IInvokable LastSubmitted;
                public Exception SubmitFailure = new InvalidOperationException("submission should not be reached");
                public FailingProxyBase(ICodecProvider codecs, CopyContextPool pool)
                {
                    CodecProvider = codecs;
                    CopyContextPool = pool;
                }
                protected T GetInvokable<T>() => Activator.CreateInstance<T>();
                protected ValueTask InvokeAsync(IInvokable request)
                {
                    LastSubmitted = request;
                    throw SubmitFailure;
                }
                protected ValueTask<T> InvokeAsync<T>(IInvokable request)
                {
                    LastSubmitted = request;
                    throw SubmitFailure;
                }
                protected void Invoke(IInvokable request)
                {
                    LastSubmitted = request;
                    throw SubmitFailure;
                }
            }

            [GenerateMethodSerializers(typeof(FailingProxyBase))]
            public interface IFaultCalls
            {
                ValueTask Submit([DisposeOnCompletion] ArcBuffer first, [DisposeOnCompletion] ArcBuffer second);
            }
        }
        """;
}
