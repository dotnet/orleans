using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using UnitTests.GrainReferences;

namespace Orleans.NativeAotSmoke;

internal static class Program
{
    private static void Main()
    {
        ValidateGeneratedProxy();
        ValidateClosedGenericFactories();
        ValidateNonGrainFactory();
        ValidateSharedState(unordered: false);
        ValidateSharedState(unordered: true);
        ValidateConstructorFailures();
        ValidateNonPublicConstructor();
        Console.WriteLine("Production grain-reference provider constructed generated proxies under NativeAOT.");
    }

    private static void ValidateNonGrainFactory()
    {
        using var fixture = new ReferenceConstructionFixture();
        var registration = fixture.ManifestOptions
            .GetOrCreate<InterfaceProxyFactoryOptions<Func<string, int, NonGrainProxyBase>>>()
            .Factories[typeof(INonGrainProxy)];
        var factory = registration.Factory ?? throw new InvalidOperationException("The non-grain factory was not registered.");
        var proxy = factory("non-grain", 42);

        Ensure(proxy is INonGrainProxy, "The non-grain factory did not construct the generated interface proxy.");
        Ensure(proxy.Label == "non-grain" && proxy.Number == 42, "The non-grain factory changed its arguments.");
        Ensure(registration.ProxyType == proxy.GetType(), "The non-grain factory changed its registered proxy type.");
    }

    private static void ValidateClosedGenericFactories()
    {
        using var fixture = new ReferenceConstructionFixture();
        var number = fixture.CreateReference<int>("number-key");
        var text = fixture.CreateReference<string>("text-key");
        var repeated = fixture.CreateReference<int>("number-key");

        Ensure(number is IGenericConstructionGrain<int>, "The statically closed value-type proxy was not constructed.");
        Ensure(text is IGenericConstructionGrain<string>, "The statically closed reference-type proxy was not constructed.");
        Ensure(number.GetType() != text.GetType(), "Closed proxy types must retain their type arguments.");
        Ensure(number.InterfaceType == fixture.Resolver.GetGrainInterfaceType(typeof(IGenericConstructionGrain<int>)), "Closed value-type interface identity changed.");
        Ensure(text.InterfaceType == fixture.Resolver.GetGrainInterfaceType(typeof(IGenericConstructionGrain<string>)), "Closed reference-type interface identity changed.");
        Ensure(number.GrainId.Key == IdSpan.Create("number-key") && text.GrainId.Key == IdSpan.Create("text-key"), "Closed generic grain keys changed.");
        Ensure(!ReferenceEquals(number, repeated) && number.Equals(repeated), "Closed generic reference identity changed.");
    }

    [GenerateProxyFactory(typeof(Func<string, int, NonGrainProxyBase>))]
    public class NonGrainProxyBase(string label, int number)
    {
        public string Label { get; } = label;
        public int Number { get; } = number;

        public ValueTask<T> InvokeAsync<T>(IInvokable request) => throw new NotSupportedException();
        public ValueTask InvokeAsync(IInvokable request) => throw new NotSupportedException();
    }

    [GenerateMethodSerializers(typeof(NonGrainProxyBase))]
    public interface INonGrainProxy;

    private static void ValidateGeneratedProxy()
    {
        using var fixture = new ReferenceConstructionFixture();
        var first = fixture.CreateReference("first-key");
        var second = fixture.CreateReference("second-key");
        var repeated = fixture.CreateReference("first-key");

        Ensure(first is IConstructionGrain and IConstructionBaseGrain, "Generated proxy interfaces changed.");
        Ensure(first.GetType() == second.GetType(), "The production provider selected different proxy types.");
        Ensure(first.GrainId == GrainId.Create(ReferenceConstructionFixture.GrainType, IdSpan.Create("first-key")), "First grain id changed.");
        Ensure(second.GrainId.Key == IdSpan.Create("second-key"), "Second grain key changed.");
        Ensure(first.InterfaceType == ReferenceConstructionFixture.InterfaceType, "Grain interface identity changed.");
        Ensure(first.InterfaceVersion == 17, "Grain interface version changed.");
        Ensure(!ReferenceEquals(first, repeated) && first.Equals(repeated), "Reference instance/equality semantics changed.");
        Ensure(ReferenceEquals(first.Cast<IConstructionGrain>(), first), "Generated proxy runtime cast changed.");
        Ensure(ReferenceEquals(fixture.Runtime.CastReference, first), "The configured reference runtime was not used.");
        Ensure(fixture.Runtime.CastInterface == typeof(IConstructionGrain), "Runtime cast interface changed.");
        Ensure(
            !fixture.Provider.TryGet(ReferenceConstructionFixture.GrainType, GrainInterfaceType.Create("unknown"), out var missing)
            && missing is null,
            "Unknown interfaces must be declined by the production provider.");
    }

    private static void ValidateSharedState(bool unordered)
    {
        using var fixture = new ReferenceConstructionFixture(
            unordered, proxyType: typeof(InspectableConstructionProxy),
            factory: static (shared, key) => new InspectableConstructionProxy(shared, key), includeGeneratedFactories: true);
        var first = (InspectableConstructionProxy)fixture.CreateReference("first-key");
        var second = (InspectableConstructionProxy)fixture.CreateReference("second-key");
        var shared = first.ConstructionShared;

        Ensure(ReferenceEquals(shared, second.ConstructionShared), "References must reuse the activator's shared state.");
        Ensure(ReferenceEquals(shared.Runtime, fixture.Runtime), "Shared reference runtime identity changed.");
        Ensure(ReferenceEquals(shared.ServiceProvider, fixture.Services), "Shared service provider identity changed.");
        Ensure(ReferenceEquals(shared.CodecProvider, fixture.Services.GetRequiredService<CodecProvider>()), "Shared codec provider identity changed.");
        Ensure(ReferenceEquals(shared.CopyContextPool, fixture.Services.GetRequiredService<CopyContextPool>()), "Shared copy context pool identity changed.");
        Ensure(shared.GrainType == ReferenceConstructionFixture.GrainType, "Shared grain type changed.");
        Ensure(shared.InterfaceType == ReferenceConstructionFixture.InterfaceType, "Shared interface type changed.");
        Ensure(shared.InterfaceVersion == 17, "Shared interface version changed.");
        Ensure(shared.InvokeMethodOptions == (unordered ? InvokeMethodOptions.Unordered : InvokeMethodOptions.None), "Invocation options changed.");
    }

    private static void ValidateConstructorFailures()
    {
        using var invalid = new ReferenceConstructionFixture(unordered: false, proxyType: typeof(MissingConstructorProxy));
        ExpectException<SerializerException>(
            () => invalid.CreateReference("key"),
            "Invalid proxy type: " + typeof(MissingConstructorProxy));

        using var throwing = new ReferenceConstructionFixture(
            unordered: false, proxyType: typeof(ThrowingConstructorProxy),
            factory: static (shared, key) => new ThrowingConstructorProxy(shared, key), includeGeneratedFactories: true);
        ExpectException<ConstructionException>(() => throwing.CreateReference("key"), "proxy-constructor");
    }

    private static void ValidateNonPublicConstructor()
    {
        using var fixture = new ReferenceConstructionFixture(
            unordered: false, proxyType: typeof(NonPublicConstructorProxy), factory: NonPublicConstructorProxy.Create);
        var reference = fixture.CreateReference("private-constructor");
        Ensure(reference is NonPublicConstructorProxy, "The preserved non-public proxy constructor was not invoked.");
        Ensure(reference.GrainId.Key == IdSpan.Create("private-constructor"), "The non-public constructor changed the grain key.");
    }

    private static void ExpectException<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception) when (exception.GetType() == typeof(TException) && exception.Message == message)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException)}: {message}");
    }

    private static void Ensure([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
