using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Diagnostics;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

public class CustomReturnTypeTests
{
    [Fact]
    public async Task GenericReturnType_UsesRegisteredBaseAndInitializer()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(CustomRequest<>))]
            public readonly struct CustomCall<T> { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest<T>
            {
                public CustomCall<T> InitializeRequest(GrainReference proxy) => new();
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall<int> Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        var generated = GetGeneratedSource(result);
        Assert.Contains(": global::CustomRequest<int>", generated);
        Assert.Contains("return request.InitializeRequest(this);", generated);
    }

    [Fact]
    public async Task NonGenericReturnType_UsesRegisteredBase()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest
            {
                public CustomCall InitializeRequest(GrainReference proxy) => new();
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(": global::CustomRequest", GetGeneratedSource(result));
    }

    [Fact]
    public async Task ExactClosedRegistration_PrecedesOpenGenericRegistration()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(CustomRequest<>))]
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<int>), typeof(IntRequest))]
            public class CustomCall<T> { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest<T>
            {
                public CustomCall<T> InitializeRequest(GrainReference proxy) => new();
            }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class IntRequest
            {
                public CustomCall<int> InitializeRequest(GrainReference proxy) => new();
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall<int> Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        var generated = GetGeneratedSource(result);
        Assert.Contains(": global::IntRequest", generated);
        Assert.DoesNotContain(": global::CustomRequest<int>", generated);
    }

    [Fact]
    public async Task ClosedRegistration_DoesNotMatchDifferentGenericConstructionRegardlessOfReferenceOrder()
    {
        var owner = await CompileReference("""
            namespace Owner;
            public class CustomCall<T> { }
            """, "ReturnTypeOwner");
        var closedAdapter = await CompileReference(CommonTypes + """
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall<string>),
                typeof(ClosedAdapter.StringRequest))]

            namespace ClosedAdapter
            {
                public abstract class StringRequest { }
            }
            """, "ClosedAdapter", owner);
        var openAdapter = await CompileReference(CommonTypes + """
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall<>),
                typeof(OpenAdapter.CustomRequest<>))]

            namespace OpenAdapter
            {
                public abstract class CustomRequest<T> { }
            }
            """, "OpenAdapter", owner);
        var source = CommonTypes + """
            public interface ICustomGrain : IGrainWithStringKey
            {
                Owner.CustomCall<int> Call();
            }
            """;

        var forward = await RunGenerator(source, owner, closedAdapter, openAdapter);
        var reverse = await RunGenerator(source, owner, openAdapter, closedAdapter);

        Assert.Empty(forward.Diagnostics);
        Assert.Empty(reverse.Diagnostics);
        Assert.Contains(": global::OpenAdapter.CustomRequest<int>", GetGeneratedSource(forward));
        Assert.Contains(": global::OpenAdapter.CustomRequest<int>", GetGeneratedSource(reverse));
    }

    [Fact]
    public async Task MethodRegistration_PrecedesReturnTypeRegistration()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(CustomRequest<>))]
            public class CustomCall<T> { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest<T>
            {
                public CustomCall<T> InitializeRequest(GrainReference proxy) => new();
            }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class OverrideRequest<T>
            {
                public CustomCall<T> InitializeRequest(GrainReference proxy) => new();
            }

            [AttributeUsage(AttributeTargets.Method)]
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(OverrideRequest<>))]
            public sealed class UseOverrideAttribute : Attribute { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                [UseOverride]
                CustomCall<int> Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(": global::OverrideRequest<int>", GetGeneratedSource(result));
    }

    [Theory]
    [InlineData(
        "typeof(CustomCall<>), typeof(BadRequest<,>)",
        "has arity 2, but return type 'CustomCall<>' has arity 1")]
    [InlineData(
        "typeof(CustomCall<>), typeof(ConstrainedRequest<>)",
        "does not satisfy the constraints")]
    public async Task InvalidGenericRegistration_ProducesDeterministicDiagnostic(string mapping, string expectedMessage)
    {
        var result = await RunGenerator(CommonTypes + $$"""
            [InvokableBaseType(typeof(GrainReference), {{mapping}})]
            public class CustomCall<T> { }

            public abstract class BadRequest<T1, T2> { }
            public abstract class ConstrainedRequest<T> where T : class { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall<int> Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains(expectedMessage, diagnostic.GetMessage());
    }

    [Fact]
    public async Task OpenReturnMapping_RejectsClosedInvokableBase()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(
                typeof(GrainReference),
                typeof(CustomCall<>),
                typeof(CustomRequest<string>))]
            public class CustomCall<T> { }

            public abstract class CustomRequest<T> { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall<int> Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("requires an unbound generic invokable base type", diagnostic.GetMessage());
        Assert.Contains("CustomRequest<string>", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("int?", "where T : struct")]
    [InlineData("AbstractValue", "where T : new()")]
    [InlineData("string", "where T : unmanaged")]
    [InlineData("string?", "where T : notnull")]
    [InlineData("string?", "where T : class")]
    [InlineData("PlainValue", "where T : IMarker")]
    public async Task InvalidTypeArgumentConstraints_ProduceDiagnostic(string typeArgument, string constraints)
    {
        var result = await RunGenerator("#nullable enable\n" + CommonTypes + $$"""
            [InvokableBaseType(
                typeof(GrainReference),
                typeof(CustomCall<>),
                typeof(ConstrainedRequest<>))]
            public class CustomCall<T> { }

            public interface IMarker { }
            public class PlainValue { }
            public abstract class AbstractValue
            {
                public AbstractValue() { }
            }

            public abstract class ConstrainedRequest<T> {{constraints}} { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall<{{typeArgument}}> Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("does not satisfy", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("string?", "where T : class?")]
    [InlineData("int", "where T : struct")]
    [InlineData("int", "where T : unmanaged")]
    [InlineData("GoodValue", "where T : class, IMarker, new()")]
    public async Task ValidTypeArgumentConstraints_AreAccepted(string typeArgument, string constraints)
    {
        var result = await RunGenerator("#nullable enable\n" + CommonTypes + $$"""
            [InvokableBaseType(
                typeof(GrainReference),
                typeof(CustomCall<>),
                typeof(ConstrainedRequest<>))]
            public class CustomCall<T> { }

            public interface IMarker { }
            public class GoodValue : IMarker
            {
                public GoodValue() { }
            }

            public abstract class ConstrainedRequest<T> {{constraints}} { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall<{{typeArgument}}> Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("ConstrainedRequest", GetGeneratedSource(result));
    }

    [Fact]
    public async Task NonNullableBaseClassConstraint_SatisfiesNotNullConstraint()
    {
        var result = await RunGenerator("#nullable enable\n" + CommonTypes + """
            [InvokableBaseType(
                typeof(GrainReference),
                typeof(CustomCall<>),
                typeof(ConstrainedRequest<>))]
            public class CustomCall<T> { }

            public class BaseValue { }
            public abstract class ConstrainedRequest<T> where T : notnull { }

            public interface ICustomGrain<T> : IGrainWithStringKey where T : BaseValue
            {
                CustomCall<T> Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(": global::ConstrainedRequest<T>", GetGeneratedSource(result));
    }

    [Fact]
    public async Task RecursiveArrayConstraint_IsSubstituted()
    {
        var result = await RunGenerator(CommonTypes + """
            using System.Collections.Generic;

            [InvokableBaseType(
                typeof(GrainReference),
                typeof(CustomCall<>),
                typeof(ConstrainedRequest<>))]
            public class CustomCall<T> { }

            public sealed class Recursive : List<Recursive[]> { }
            public abstract class ConstrainedRequest<T> where T : IEnumerable<T[]> { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall<Recursive> Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(": global::ConstrainedRequest<global::Recursive>", GetGeneratedSource(result));
    }

    [Fact]
    public async Task RecursiveTupleConstraint_IsSubstituted()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(
                typeof(GrainReference),
                typeof(CustomCall<>),
                typeof(ConstrainedRequest<>))]
            public class CustomCall<T> { }

            public interface IRecursive<T> { }
            public sealed class Recursive : IRecursive<(Recursive, Recursive[])> { }
            public abstract class ConstrainedRequest<T> where T : IRecursive<(T, T[])> { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall<Recursive> Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(": global::ConstrainedRequest<global::Recursive>", GetGeneratedSource(result));
    }

    [Fact]
    public async Task InaccessibleBase_ProducesDiagnosticAtRegistration()
    {
        var result = await RunGenerator(CommonTypes + """
            public class Holder
            {
                [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(HiddenRequest))]
                public class CustomCall { }

                private abstract class HiddenRequest { }
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                Holder.CustomCall Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("is not accessible", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InvalidInitializer_ProducesDiagnostic()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest
            {
                public int InitializeRequest(string proxy) => 0;
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("must be an accessible, concrete", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("public abstract CustomCall InitializeRequest(GrainReference proxy);")]
    [InlineData("public CustomCall InitializeRequest<T>(T proxy) => new();")]
    [InlineData("public CustomCall InitializeRequest(ref GrainReference proxy) => new();")]
    [InlineData("public CustomCall InitializeRequest(in GrainReference proxy) => new();")]
    [InlineData("private CustomCall InitializeRequest(GrainReference proxy) => new();")]
    [InlineData("public static CustomCall InitializeRequest(GrainReference proxy) => new();")]
    public async Task InvalidInitializerShape_ProducesDiagnostic(string initializer)
    {
        var result = await RunGenerator(CommonTypes + $$"""
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest
            {
                {{initializer}}
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("concrete, non-generic", diagnostic.GetMessage());
        Assert.Contains("one by-value parameter", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData(
        "public object InitializeRequest(GrainReference proxy) => new();",
        "public CustomCall InitializeRequest(object proxy) => new();")]
    [InlineData(
        "public CustomCall InitializeRequest(object proxy) => new();",
        "public object InitializeRequest(GrainReference proxy) => new();")]
    public async Task InitializerOverloadResolution_RejectsSelectedIncompatibleReturnRegardlessOfDeclarationOrder(
        string first,
        string second)
    {
        var result = await RunGenerator(CommonTypes + $$"""
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest
            {
                {{first}}
                {{second}}
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("must be an accessible", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InitializerOverloadResolution_AcceptsSelectedCompatibleOverload()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest
            {
                public object InitializeRequest(object proxy) => new();
                public CustomCall InitializeRequest(GrainReference proxy) => new();
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("return request.InitializeRequest(this);", GetGeneratedSource(result));
    }

    [Fact]
    public async Task InitializerOverloadResolution_RejectsAmbiguousGeneratedProxyConversions()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest
            {
                public CustomCall InitializeRequest(GrainReference proxy) => new();
                public CustomCall InitializeRequest(IGrain proxy) => new();
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("must be an accessible", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ReferencedInitializerOverloadResolution_IsIndependentOfReferenceOrder()
    {
        var owner = await CompileReference(CommonTypes + """
            namespace Owner
            {
                public class CustomCall { }

                [ReturnValueProxy(nameof(InitializeRequest))]
                public abstract class CustomRequest
                {
                    public CustomCall InitializeRequest(object proxy) => new();
                    public object InitializeRequest(GrainReference proxy) => new();
                }
            }
            """, "ReturnTypeOwner");
        var adapter = await CompileReference(CommonTypes + """
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall),
                typeof(Owner.CustomRequest))]
            """, "ReturnTypeAdapter", owner);
        var source = CommonTypes + """
            public interface ICustomGrain : IGrainWithStringKey
            {
                Owner.CustomCall Call();
            }
            """;

        var forward = await RunGenerator(source, owner, adapter);
        var reverse = await RunGenerator(source, adapter, owner);

        var forwardDiagnostic = Assert.Single(forward.Diagnostics);
        var reverseDiagnostic = Assert.Single(reverse.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, forwardDiagnostic.Id);
        Assert.Equal(forwardDiagnostic.GetMessage(), reverseDiagnostic.GetMessage());
    }

    [Fact]
    public async Task GeneratedActivatorConstructor_UsesDependencyInjectionActivation()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }
            public interface IService { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest
            {
                [GeneratedActivatorConstructor]
                protected CustomRequest(IService service) { }
                public CustomCall InitializeRequest(GrainReference proxy) => new();
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("GetInvokable<", GetGeneratedSource(result));
    }

    [Theory]
    [InlineData("protected CustomRequest(string value = \"\") { }")]
    [InlineData("protected CustomRequest(params string[] values) { }")]
    [InlineData("protected CustomRequest(string required) { } protected CustomRequest(int optional = 0) { }")]
    [InlineData("protected CustomRequest(int optional = 0) { } protected CustomRequest(params string[] values) { }")]
    public async Task BaseConstructorInvocableWithNoArguments_IsAccepted(string constructors)
    {
        var result = await RunGenerator(CommonTypes + $$"""
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            public abstract class CustomRequest
            {
                {{constructors}}
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(": global::CustomRequest", GetGeneratedSource(result));
    }

    [Theory]
    [InlineData("private CustomRequest() { }")]
    [InlineData("protected CustomRequest(string value) { }")]
    [InlineData("protected CustomRequest(string required) { } private CustomRequest(int optional = 0) { }")]
    [InlineData("protected CustomRequest(string value = \"\") { } protected CustomRequest(int value = 0) { }")]
    public async Task UnusableBaseConstructor_ProducesDiagnosticAtRegistration(string constructor)
    {
        var result = await RunGenerator(CommonTypes + $$"""
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            public abstract class CustomRequest
            {
                {{constructor}}
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("accessible parameterless constructor", diagnostic.GetMessage());
        Assert.Equal("InvokableBaseType", diagnostic.Location.SourceTree!.GetText()
            .ToString(diagnostic.Location.SourceSpan)
            .Split('(')[0]
            .TrimStart('['));
    }

    [Fact]
    public async Task InaccessibleGeneratedActivatorConstructor_ProducesDiagnosticAtRegistration()
    {
        var owner = await CompileReference(CommonTypes + """
            namespace Owner
            {
                public class CustomCall { }
                public interface IService { }

                public abstract class CustomRequest
                {
                    [GeneratedActivatorConstructor]
                    internal CustomRequest(IService service) { }
                }
            }
            """, "ReturnTypeOwner");
        var result = await RunGenerator(CommonTypes + """
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall),
                typeof(Owner.CustomRequest))]

            public interface ICustomGrain : IGrainWithStringKey
            {
                Owner.CustomCall Call();
            }
            """, owner);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("accessible parameterless constructor", diagnostic.GetMessage());
        Assert.NotEqual(Location.None, diagnostic.Location);
        Assert.NotNull(diagnostic.Location.SourceTree);
    }

    [Fact]
    public async Task ReferencedAdapterAssembly_IsDiscovered()
    {
        var owner = await CompileReference("""
            namespace Owner;
            public class CustomCall<T> { }
            """, "ReturnTypeOwner");
        var adapter = await CompileReference(CommonTypes + """
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall<>),
                typeof(Adapter.CustomRequest<>))]

            namespace Adapter
            {
                [ReturnValueProxy(nameof(InitializeRequest))]
                public abstract class CustomRequest<T>
                {
                    public Owner.CustomCall<T> InitializeRequest(GrainReference proxy) => new();
                }
            }
            """, "Adapter", owner);

        var result = await RunGenerator(CommonTypes + """
            public interface ICustomGrain : IGrainWithStringKey
            {
                Owner.CustomCall<int> Call();
            }
            """, owner, adapter);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(": global::Adapter.CustomRequest<int>", GetGeneratedSource(result));
    }

    [Fact]
    public async Task ConflictingReferencedAdapters_AreIndependentOfReferenceOrder()
    {
        var owner = await CompileReference("""
            namespace Owner;
            public class CustomCall { }
            """, "ReturnTypeOwner");
        var adapterA = await CompileAdapter(owner, "AdapterA");
        var adapterB = await CompileAdapter(owner, "AdapterB");

        var source = CommonTypes + """
            public interface ICustomGrain : IGrainWithStringKey
            {
                Owner.CustomCall Call();
            }
            """;
        var forward = await RunGenerator(source, owner, adapterA, adapterB);
        var reverse = await RunGenerator(source, owner, adapterB, adapterA);

        var forwardDiagnostic = Assert.Single(forward.Diagnostics);
        var reverseDiagnostic = Assert.Single(reverse.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, forwardDiagnostic.Id);
        Assert.Equal(forwardDiagnostic.GetMessage(), reverseDiagnostic.GetMessage());
        Assert.Contains("AdapterA.CustomRequest", forwardDiagnostic.GetMessage());
        Assert.Contains("AdapterB.CustomRequest", forwardDiagnostic.GetMessage());
    }

    [Fact]
    public async Task IdenticalReferencedAdapterRegistrations_Coalesce()
    {
        var owner = await CompileReference(CommonTypes + """
            namespace Owner
            {
                public class CustomCall { }

                [ReturnValueProxy(nameof(InitializeRequest))]
                public abstract class CustomRequest
                {
                    public CustomCall InitializeRequest(GrainReference proxy) => new();
                }
            }
            """, "ReturnTypeOwner");
        const string registration = """
            using Orleans;
            using Orleans.Runtime;
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall),
                typeof(Owner.CustomRequest))]
            """;
        var adapterA = await CompileReference(registration, "AdapterA", owner);
        var adapterB = await CompileReference(registration, "AdapterB", owner);

        var result = await RunGenerator(CommonTypes + """
            public interface ICustomGrain : IGrainWithStringKey
            {
                Owner.CustomCall Call();
            }
            """, owner, adapterB, adapterA);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(": global::Owner.CustomRequest", GetGeneratedSource(result));
    }

    [Fact]
    public async Task SameNamedTypesFromDifferentAssemblies_ConflictIndependentOfReferenceOrder()
    {
        var owner = await CompileReference("""
            namespace Owner;
            public class CustomCall { }
            """, "ReturnTypeOwner");
        var baseSource = CommonTypes + """
            namespace Shared
            {
                [ReturnValueProxy(nameof(InitializeRequest))]
                public abstract class CustomRequest
                {
                    public Owner.CustomCall InitializeRequest(GrainReference proxy) => new();
                }
            }
            """;
        var baseA = await CompileReference(baseSource, "SameNameBaseA", owner);
        var baseB = await CompileReference(baseSource, "SameNameBaseB", owner);
        const string registration = """
            using Orleans;
            using Orleans.Runtime;
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall),
                typeof(Shared.CustomRequest))]
            """;
        var adapterA = await CompileReference(registration, "SameNameAdapterA", owner, baseA);
        var adapterB = await CompileReference(registration, "SameNameAdapterB", owner, baseB);
        var source = CommonTypes + """
            public interface ICustomGrain : IGrainWithStringKey
            {
                Owner.CustomCall Call();
            }
            """;

        var forward = await RunGenerator(source, owner, baseA, baseB, adapterA, adapterB);
        var reverse = await RunGenerator(source, owner, baseB, baseA, adapterB, adapterA);

        var forwardDiagnostic = Assert.Single(forward.Diagnostics);
        var reverseDiagnostic = Assert.Single(reverse.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, forwardDiagnostic.Id);
        Assert.Equal(forwardDiagnostic.GetMessage(), reverseDiagnostic.GetMessage());
        Assert.Contains("Shared.CustomRequest [SameNameBaseA", forwardDiagnostic.GetMessage());
        Assert.Contains("Shared.CustomRequest [SameNameBaseB", forwardDiagnostic.GetMessage());
    }

    [Fact]
    public async Task AssemblyRegistration_CannotReplaceProxyDefault()
    {
        var result = await RunGenerator(CommonTypes + """
            using System.Threading.Tasks;

            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Task),
                typeof(CustomRequest))]

            public abstract class CustomRequest { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                Task Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("cannot replace proxy default", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RegistrationsAreScopedToEachProxyBase()
    {
        var source = CommonTypes + """
            using System.Threading.Tasks;
            using Orleans.Serialization.Invocation;

            [assembly: InvokableBaseType(typeof(ProxyA), typeof(CustomCall), typeof(RequestA))]
            [assembly: InvokableBaseType(typeof(ProxyB), typeof(CustomCall), typeof(RequestB))]

            public abstract class ProxyA
            {
                protected ValueTask InvokeAsync(IInvokable request) => default;
                protected ValueTask<T> InvokeAsync<T>(IInvokable request) => default;
            }

            public abstract class ProxyB
            {
                protected ValueTask InvokeAsync(IInvokable request) => default;
                protected ValueTask<T> InvokeAsync<T>(IInvokable request) => default;
            }

            public class CustomCall { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class RequestA
            {
                public CustomCall InitializeRequest(ProxyA proxy) => new();
            }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class RequestB
            {
                public CustomCall InitializeRequest(ProxyB proxy) => new();
            }

            public interface IMethods
            {
                CustomCall Call();
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var resolver = new InvokableBaseTypeResolver(compilation);
        var proxyA = compilation.GetTypeByMetadataName("ProxyA")!;
        var proxyB = compilation.GetTypeByMetadataName("ProxyB")!;
        var method = compilation.GetTypeByMetadataName("IMethods")!.GetMembers("Call").OfType<IMethodSymbol>().Single();

        Assert.True(resolver.TryResolve(proxyA, method, out var requestA, out var diagnosticA), diagnosticA?.Message);
        Assert.True(resolver.TryResolve(proxyB, method, out var requestB, out var diagnosticB), diagnosticB?.Message);
        Assert.Equal("RequestA", requestA!.Name);
        Assert.Equal("RequestB", requestB!.Name);

    }

    private const string CommonTypes = """
        using System;
        using Orleans;
        using Orleans.Invocation;
        using Orleans.Runtime;

        """;

    private static async Task<GeneratorRunResult> RunGenerator(string code, params MetadataReference[] references)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(code, "CustomReturnTypes", references);
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Results.Single();
    }

    private static async Task<MetadataReference> CompileAdapter(MetadataReference owner, string assemblyName)
        => await CompileReference(CommonTypes + $$"""
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall),
                typeof({{assemblyName}}.CustomRequest))]

            namespace {{assemblyName}}
            {
                [ReturnValueProxy(nameof(InitializeRequest))]
                public abstract class CustomRequest
                {
                    public Owner.CustomCall InitializeRequest(GrainReference proxy) => new();
                }
            }
            """, assemblyName, owner);

    private static async Task<MetadataReference> CompileReference(
        string source,
        string assemblyName,
        params MetadataReference[] references)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(source, assemblyName, references);
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static string GetGeneratedSource(GeneratorRunResult result)
        => string.Join(Environment.NewLine, result.GeneratedSources.Select(static source => source.SourceText.ToString()));
}
