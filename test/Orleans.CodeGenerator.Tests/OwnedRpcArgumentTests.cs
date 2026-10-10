using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;

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
    public async Task DefaultAndEmptyArguments_DisposeSafely(string method)
    {
        using var fixture = await GeneratedFixture.Create();
        var request = fixture.Request(method);
        request.Dispose();
        request.Dispose();
        if (method == "Owned") Assert.Null(request.GetArgument(0));
        else
        {
            Assert.Equal(0, fixture.Get<int>(request.GetArgument(0)!, "Marker"));
            Assert.Equal(0, fixture.ValueEnvelopeType.GetField("Disposes")!.GetValue(null));
        }
        Assert.Null(fixture.GetCts(request));
        Assert.Equal(CancellationToken.None, request.GetCancellationToken());
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
    public async Task AliasedOwnedArguments_DirectCopiedAndDecodedRequests_DisposeEachOwnerExactlyOnce()
    {
        using var fixture = await GeneratedFixture.Create();
        using var provider = fixture.Provider();
        var original = fixture.Envelope(137);
        var source = fixture.Request("TwoOwned");
        source.SetArgument(0, original);
        source.SetArgument(1, original);
        var copied = provider.GetRequiredService<DeepCopier>().Copy(source);
        var copiedArgument = copied.GetArgument(0)!;
        Assert.NotSame(original, copiedArgument);
        Assert.Same(copiedArgument, copied.GetArgument(1));
        Assert.Single(fixture.Get<IEnumerable<object>>(fixture.Copier(provider), "Copies"));
        var serializer = provider.GetRequiredService<Serializer<IInvokable>>();
        var decoded = Assert.IsAssignableFrom<IInvokable>(serializer.Deserialize(serializer.SerializeToArray(source)));
        var decodedArgument = decoded.GetArgument(0)!;
        Assert.NotSame(original, decodedArgument);
        Assert.NotSame(copiedArgument, decodedArgument);
        Assert.Same(decodedArgument, decoded.GetArgument(1));
        Assert.Single(fixture.Get<IEnumerable<object>>(fixture.Codec(provider), "Decoded"));

        foreach (var request in new[] { copied, decoded })
        {
            var argument = request.GetArgument(0)!;
            Assert.Equal(137, fixture.Get<int>(argument, "Marker"));
            request.Dispose();
            request.Dispose();
            Assert.Equal(1, fixture.Get<int>(argument, "Disposes"));
            Assert.Null(request.GetArgument(0));
            Assert.Null(request.GetArgument(1));
            Assert.False(Assert.IsAssignableFrom<IInvokableArgumentOwner>(request).TryRetainArgumentResources());
        }

        Assert.Equal(0, fixture.Get<int>(original, "Disposes"));
        Assert.Equal(137, fixture.Get<int>(original, "Marker"));
        Assert.Same(original, source.GetArgument(0));
        Assert.Same(original, source.GetArgument(1));
        source.Dispose();
        source.Dispose();
        Assert.Equal(1, fixture.Get<int>(original, "Disposes"));
        Assert.Null(source.GetArgument(0));
        Assert.Null(source.GetArgument(1));
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
    public async Task ActiveCopiedArgument_DisposePreservesMarker_UntilFinalRelease()
    {
        using var fixture = await GeneratedFixture.Create();
        using var provider = fixture.Provider();
        var original = fixture.Envelope(89);
        var copy = provider.GetRequiredService<DeepCopier>().Copy(original);
        var request = fixture.Request("Owned");
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        request.SetArgument(0, copy);
        Assert.True(owner.TryRetainArgumentResources());
        Assert.NotSame(original, copy);

        request.Dispose();
        owner.CompleteArgumentResources();

        Assert.False(owner.TryRetainArgumentResources());
        Assert.Same(copy, request.GetArgument(0));
        Assert.Equal(89, fixture.Get<int>(copy, "Marker"));
        Assert.Equal(0, fixture.Get<int>(copy, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(original, "Disposes"));
        owner.ReleaseArgumentResources();
        Assert.Equal(1, fixture.Get<int>(copy, "Disposes"));
        Assert.Null(request.GetArgument(0));
        request.Dispose();
        owner.CompleteArgumentResources();
        Assert.Equal(1, fixture.Get<int>(copy, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(original, "Disposes"));
        Assert.Equal(89, fixture.Get<int>(original, "Marker"));
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
    public async Task GeneratedInvokable_DisposingEnvelopeCopy_LeavesOriginalAndBorrowedCopyUntouched()
    {
        using var fixture = await GeneratedFixture.Create();
        using var provider = fixture.Provider();
        var original = fixture.Envelope(42);
        var borrowed = fixture.Envelope(128);
        var source = fixture.Request("Owned");
        source.SetArgument(0, original);
        source.SetArgument(1, borrowed);
        var request = provider.GetRequiredService<DeepCopier>().Copy(source);
        var ownedCopy = request.GetArgument(0)!;
        var borrowedCopy = request.GetArgument(1)!;
        Assert.NotSame(source, request);
        Assert.NotSame(original, ownedCopy);
        Assert.NotSame(borrowed, borrowedCopy);
        Assert.Equal(42, fixture.Get<int>(ownedCopy, "Marker"));
        Assert.Equal(128, fixture.Get<int>(borrowedCopy, "Marker"));

        request.Dispose();
        request.Dispose();

        Assert.Equal(1, fixture.Get<int>(ownedCopy, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(borrowedCopy, "Disposes"));
        Assert.Equal(128, fixture.Get<int>(borrowedCopy, "Marker"));
        Assert.Null(request.GetArgument(0));
        Assert.Null(request.GetArgument(1));
        Assert.Equal(0, fixture.Get<int>(original, "Disposes"));
        Assert.Equal(42, fixture.Get<int>(original, "Marker"));
        Assert.Equal(0, fixture.Get<int>(borrowed, "Disposes"));
        Assert.Same(original, source.GetArgument(0));
        Assert.Same(borrowed, source.GetArgument(1));
        source.Dispose();
        Assert.Equal(1, fixture.Get<int>(original, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(borrowed, "Disposes"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task GeneratedCopierFailure_ReleasesEarlierEnvelopeCopy_PreservesSourceRequestAndOriginalError(bool cleanupThrows, bool hasLogger)
    {
        using var fixture = await GeneratedFixture.Create();
        using var logger = hasLogger ? new CapturingLoggerFactory() : null;
        using var provider = fixture.Provider(logger);
        var probe = fixture.Copier(provider);
        var expected = new InvalidOperationException("later copy error");
        var cleanupFailure = cleanupThrows ? new InvalidOperationException("partial copy cleanup error") : null;
        fixture.Set(probe, "FailureMarker", 99);
        fixture.Set(probe, "Failure", expected);
        if (cleanupThrows) fixture.Set(probe, "DisposeFailure", cleanupFailure!);
        var first = fixture.Envelope(21);
        var second = fixture.Envelope(99);
        var request = fixture.Request("TwoOwned");
        request.SetArgument(0, first);
        request.SetArgument(1, second);

        var actual = Record.Exception(() => provider.GetRequiredService<DeepCopier>().Copy(request));

        AssertFailure(actual, expected, cleanupFailure, logger);
        Assert.Equal(2, fixture.Get<int>(probe, "Attempts"));
        var partial = Assert.Single(fixture.Get<IEnumerable<object>>(probe, "Copies"));
        Assert.NotSame(first, partial);
        Assert.Equal(21, fixture.Get<int>(partial, "Marker"));
        Assert.Equal(1, fixture.Get<int>(partial, "Disposes"));
        Assert.Same(first, request.GetArgument(0));
        Assert.Same(second, request.GetArgument(1));
        Assert.Equal(0, fixture.Get<int>(first, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(second, "Disposes"));
        Assert.Equal(21, fixture.Get<int>(first, "Marker"));
        Assert.Equal(99, fixture.Get<int>(second, "Marker"));
        request.Dispose();
        Assert.Equal(1, fixture.Get<int>(first, "Disposes"));
        Assert.Equal(1, fixture.Get<int>(second, "Disposes"));
        Assert.Equal(1, fixture.Get<int>(partial, "Disposes"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ProxyLaterArgumentCopyFailure_ReleasesEarlierOwner_LeavesCallerUnchanged(bool cleanupThrows, bool hasLogger)
    {
        using var fixture = await GeneratedFixture.Create();
        using var logger = hasLogger ? new CapturingLoggerFactory() : null;
        using var provider = fixture.Provider(logger);
        var probe = fixture.Copier(provider);
        var expected = new InvalidOperationException("later proxy copy error");
        var cleanupFailure = cleanupThrows ? new InvalidOperationException("proxy cleanup error") : null;
        fixture.Set(probe, "FailureMarker", 81);
        fixture.Set(probe, "Failure", expected);
        if (cleanupThrows) fixture.Set(probe, "DisposeFailure", cleanupFailure!);
        var proxy = fixture.Proxy(provider);
        var first = fixture.Envelope(25);
        var second = fixture.Envelope(81);

        var actual = Assert.Throws<TargetInvocationException>(() => fixture.Submit(proxy, first, second));

        AssertFailure(actual.InnerException, expected, cleanupFailure, logger);
        Assert.Equal(2, fixture.Get<int>(probe, "Attempts"));
        var partial = Assert.Single(fixture.Get<IEnumerable<object>>(probe, "Copies"));
        Assert.NotSame(first, partial);
        Assert.Equal(1, fixture.Get<int>(partial, "Disposes"));
        Assert.Equal(25, fixture.Get<int>(partial, "Marker"));
        Assert.Equal(0, fixture.Get<int>(first, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(second, "Disposes"));
        Assert.Equal(25, fixture.Get<int>(first, "Marker"));
        Assert.Equal(81, fixture.Get<int>(second, "Marker"));
        Assert.Null(fixture.Get<object?>(proxy, "LastSubmitted")); // Submission is not reached.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ProxySynchronousSubmitFailure_CompletesCopiedArgumentsExactlyOnce_PreservesOriginalError(bool cleanupThrows, bool hasLogger)
    {
        using var fixture = await GeneratedFixture.Create();
        using var logger = hasLogger ? new CapturingLoggerFactory() : null;
        using var provider = fixture.Provider(logger);
        var probe = fixture.Copier(provider);
        var cleanupFailure = cleanupThrows ? new InvalidOperationException("submit cleanup error") : null;
        if (cleanupThrows) fixture.Set(probe, "DisposeFailure", cleanupFailure!);
        var proxy = fixture.Proxy(provider);
        var expected = new InvalidOperationException("synchronous submit error");
        fixture.Set(proxy, "SubmitFailure", expected);
        var first = fixture.Envelope(20);
        var second = fixture.Envelope(40);

        var actual = Assert.Throws<TargetInvocationException>(() => fixture.Submit(proxy, first, second));

        AssertFailure(actual.InnerException, expected, cleanupFailure, logger);
        Assert.Equal(2, fixture.Get<int>(probe, "Attempts"));
        var copies = fixture.Get<IEnumerable<object>>(probe, "Copies").ToArray();
        Assert.Equal(2, copies.Length);
        Assert.NotSame(first, copies[0]);
        Assert.NotSame(second, copies[1]);
        Assert.Equal(20, fixture.Get<int>(copies[0], "Marker"));
        Assert.Equal(40, fixture.Get<int>(copies[1], "Marker"));
        Assert.All(copies, copy => Assert.Equal(1, fixture.Get<int>(copy, "Disposes")));
        Assert.Equal(0, fixture.Get<int>(first, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(second, "Disposes"));
        Assert.Equal(20, fixture.Get<int>(first, "Marker"));
        Assert.Equal(40, fixture.Get<int>(second, "Marker"));
        var request = Assert.IsAssignableFrom<IInvokable>(fixture.Get<object?>(proxy, "LastSubmitted"));
        Assert.Null(request.GetArgument(0));
        Assert.Null(request.GetArgument(1));
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        Assert.False(owner.TryRetainArgumentResources());
        owner.CompleteArgumentResources();
        request.Dispose();
        Assert.All(copies, copy => Assert.Equal(1, fixture.Get<int>(copy, "Disposes")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task MalformedLaterWireField_ReleasesEarlierDecodedOwner_LeavesSourceRequestAlive(bool cleanupThrows, bool hasLogger)
    {
        using var fixture = await GeneratedFixture.Create();
        using var logger = hasLogger ? new CapturingLoggerFactory() : null;
        using var provider = fixture.Provider(logger);
        var codec = fixture.Codec(provider);
        var cleanupFailure = cleanupThrows ? new InvalidOperationException("decode cleanup error") : null;
        if (cleanupThrows) fixture.Set(codec, "DisposeFailure", cleanupFailure!);
        var first = fixture.Envelope(28);
        var second = fixture.Envelope(56);
        var request = fixture.Request("TwoOwned");
        request.SetArgument(0, first);
        request.SetArgument(1, second);
        var serializer = provider.GetRequiredService<Serializer<IInvokable>>();
        var wire = serializer.SerializeToArray(request);
        var malformed = wire[..^2]; // Remove the final byte of the later Envelope payload and the object terminator.

        var actual = Record.Exception(() => serializer.Deserialize(malformed));

        var expected = Assert.IsType<InvalidOperationException>(fixture.Get<Exception>(codec, "Failure"));
        Assert.Equal("Insufficient data present in buffer.", expected.Message);
        AssertFailure(actual, expected, cleanupFailure, logger);
        Assert.Equal(2, fixture.Get<int>(codec, "Reads"));
        var decoded = Assert.Single(fixture.Get<IEnumerable<object>>(codec, "Decoded"));
        Assert.NotSame(first, decoded);
        Assert.Equal(28, fixture.Get<int>(decoded, "Marker"));
        Assert.Equal(1, fixture.Get<int>(decoded, "Disposes"));
        Assert.Same(first, request.GetArgument(0));
        Assert.Same(second, request.GetArgument(1));
        Assert.Equal(0, fixture.Get<int>(first, "Disposes"));
        Assert.Equal(0, fixture.Get<int>(second, "Disposes"));
        Assert.Equal(28, fixture.Get<int>(first, "Marker"));
        Assert.Equal(56, fixture.Get<int>(second, "Marker"));
        request.Dispose();
        Assert.Equal(1, fixture.Get<int>(first, "Disposes"));
        Assert.Equal(1, fixture.Get<int>(second, "Disposes"));
        Assert.Equal(1, fixture.Get<int>(decoded, "Disposes"));
    }

    private static void AssertFailure(Exception? actual, Exception expected, Exception? cleanupFailure, CapturingLoggerFactory? logger)
    {
        if (cleanupFailure is not null && logger is null)
        {
            var aggregate = Assert.IsType<AggregateException>(actual);
            Assert.Collection(aggregate.InnerExceptions,
                error => Assert.Same(expected, error),
                error => Assert.Same(cleanupFailure, error));
            return;
        }

        Assert.Same(expected, actual);
        if (logger is null) return;
        if (cleanupFailure is null)
        {
            Assert.Empty(logger.Entries);
            return;
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Orleans.Serialization.Invocation", entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(cleanupFailure, entry.Exception);
        Assert.Equal("Error releasing explicitly owned RPC argument resources", entry.Message);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(string Category, LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerFactory factory, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                factory.Entries.Add((category, logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class GeneratedFixture : IDisposable
    {
        private readonly AssemblyLoadContext _loadContext;
        private readonly Assembly _assembly;
        private readonly ClassDeclarationSyntax[] _declarations;
        public CSharpCompilation Input { get; }
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
        public ServiceProvider Provider(CapturingLoggerFactory? logger = null)
        {
            var services = new ServiceCollection();
            if (logger is not null) services.AddSingleton<ILoggerFactory>(logger);
            services.AddSingleton(_assembly.GetType("OwnershipFixtures.EnvelopeCopier", throwOnError: true)!);
            services.AddSingleton(_assembly.GetType("OwnershipFixtures.EnvelopeCodec", throwOnError: true)!);
            services.AddSerializer(builder => builder.AddAssembly(_assembly));
            var provider = services.BuildServiceProvider();
            Assert.Same(logger, provider.GetService<ILoggerFactory>());
            return provider;
        }
        public object Copier(IServiceProvider provider) =>
            provider.GetRequiredService(_assembly.GetType("OwnershipFixtures.EnvelopeCopier", throwOnError: true)!);
        public object Codec(IServiceProvider provider) =>
            provider.GetRequiredService(_assembly.GetType("OwnershipFixtures.EnvelopeCodec", throwOnError: true)!);
        public object Proxy(IServiceProvider provider)
        {
            var type = _assembly.GetTypes().Single(type => type.Name == "Proxy_IFaultCalls");
            return Activator.CreateInstance(type, provider.GetRequiredService<ICodecProvider>(), provider.GetRequiredService<CopyContextPool>())!;
        }
        public void Submit(object proxy, object first, object second) =>
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
        using System.Buffers;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Orleans;
        using Orleans.Runtime;
        using Orleans.Serialization.Buffers;
        using Orleans.Serialization.Cloning;
        using Orleans.Serialization.Codecs;
        using Orleans.Serialization.Invocation;
        using Orleans.Serialization.Serializers;
        using Orleans.Serialization.WireProtocol;

        namespace OwnershipFixtures
        {
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

            [RegisterCopier]
            public sealed class EnvelopeCopier : IDeepCopier<Envelope>
            {
                public int Attempts;
                public int FailureMarker = -1;
                public Exception Failure;
                public Exception DisposeFailure;
                public List<Envelope> Copies = new();
                public Envelope DeepCopy(Envelope input, CopyContext context)
                {
                    if (context.TryGetCopy(input, out Envelope existing)) return existing;
                    Attempts++;
                    if (input.Marker == FailureMarker) throw Failure;
                    var result = new Envelope { Marker = input.Marker, Failure = DisposeFailure };
                    context.RecordCopy(input, result);
                    Copies.Add(result);
                    return result;
                }
            }

            [RegisterSerializer]
            public sealed class EnvelopeCodec : IFieldCodec<Envelope>
            {
                public int Reads;
                public Exception Failure;
                public Exception DisposeFailure;
                public List<Envelope> Decoded = new();
                public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Envelope value)
                    where TBufferWriter : IBufferWriter<byte>
                {
                    if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, typeof(Envelope), value)) return;
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Envelope), WireType.LengthPrefixed);
                    writer.WriteVarUInt32(4);
                    writer.WriteInt32(value.Marker);
                }
                public Envelope ReadValue<TInput>(ref Reader<TInput> reader, Field field)
                {
                    if (field.IsReference) return ReferenceCodec.ReadReference<Envelope, TInput>(ref reader, field);
                    Reads++;
                    try
                    {
                        field.EnsureWireType(WireType.LengthPrefixed);
                        if (reader.ReadVarUInt32() != 4) throw new InvalidOperationException("invalid envelope length");
                        var result = new Envelope { Marker = reader.ReadInt32(), Failure = DisposeFailure };
                        ReferenceCodec.RecordObject(reader.Session, result);
                        Decoded.Add(result);
                        return result;
                    }
                    catch (Exception error)
                    {
                        Failure = error;
                        throw;
                    }
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
                ValueTask Unowned(Envelope borrowed);
                ValueTask TwoOwned([DisposeOnCompletion] Envelope first, [DisposeOnCompletion] Envelope second, CancellationToken cancellationToken);
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
                ValueTask Submit([DisposeOnCompletion] Envelope first, [DisposeOnCompletion] Envelope second);
            }
        }
        """;
}
