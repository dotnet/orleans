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
    public async Task ShadowedGenericParameterNames_AreAlphaRenamedAcrossAllScopes()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(CustomRequest<>))]
            public readonly struct CustomCall<T> { }

            public abstract class CustomRequest<T> { }

            public class Scope<T> where T : class
            {
                public interface ICustomGrain<T> : IGrainWithStringKey where T : class
                {
                    CustomCall<U> Call<T, U>() where T : class where U : T;
                }
            }
            """);

        Assert.Empty(result.Diagnostics);
        var generated = GetGeneratedSource(result);
        Assert.Contains("<T, T_1, T_2, U>", generated);
        Assert.Contains(": global::CustomRequest<U>", generated);
        Assert.Contains("where U : T_2", generated);
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
    public async Task LowerPrecedenceExactRegistration_PrecedesHigherPrecedenceOpenRegistration()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<int>), typeof(IntRequest))]
            public class CustomCall<T> { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class OpenRequest<T>
            {
                public CustomCall<T> InitializeRequest(GrainReference proxy) => new();
            }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class IntRequest
            {
                public CustomCall<int> InitializeRequest(GrainReference proxy) => new();
            }

            [AttributeUsage(AttributeTargets.Method)]
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(OpenRequest<>))]
            public sealed class UseOpenMappingAttribute : Attribute { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                [UseOpenMapping]
                CustomCall<int> Call();
            }
            """);

        Assert.Empty(result.Diagnostics);
        var generated = GetGeneratedSource(result);
        Assert.Contains(": global::IntRequest", generated);
        Assert.DoesNotContain(": global::OpenRequest<int>", generated);
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

    [Fact]
    public async Task AssemblyFiltering_PreservesExactOpenAndSourcePrecedence()
    {
        var result = await RunGenerator(CommonTypes + """
            [assembly: InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(AssemblyRequest<>))]
            [assembly: InvokableBaseType(typeof(GrainReference), typeof(CustomCall<int>), typeof(IntRequest))]

            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(ReturnTypeRequest<>))]
            public class CustomCall<T> { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class AssemblyRequest<T>
            {
                public CustomCall<T> InitializeRequest(GrainReference proxy) => new();
            }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class IntRequest
            {
                public CustomCall<int> InitializeRequest(GrainReference proxy) => new();
            }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class ReturnTypeRequest<T>
            {
                public CustomCall<T> InitializeRequest(GrainReference proxy) => new();
            }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class MethodRequest<T>
            {
                public CustomCall<T> InitializeRequest(GrainReference proxy) => new();
            }

            [AttributeUsage(AttributeTargets.Method)]
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall<>), typeof(MethodRequest<>))]
            public sealed class UseMethodMappingAttribute : Attribute { }

            public interface ICustomGrain : IGrainWithStringKey
            {
                [UseMethodMapping]
                CustomCall<int> ExactAssemblyMappingWins();

                [UseMethodMapping]
                CustomCall<string> MethodMappingWins();

                CustomCall<long> ReturnTypeMappingWins();
            }
            """);

        Assert.Empty(result.Diagnostics);
        var generated = GetGeneratedSource(result);
        Assert.Contains(": global::IntRequest", generated);
        Assert.Contains(": global::MethodRequest<string>", generated);
        Assert.Contains(": global::ReturnTypeRequest<long>", generated);
        Assert.DoesNotContain(": global::AssemblyRequest<", generated);
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
    public async Task InheritedMethodInitializer_IsValidatedForEachDerivedProxyReceiver()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            [ReturnValueProxy(nameof(InitializeRequest))]
            public abstract class CustomRequest
            {
                public CustomCall InitializeRequest(IBase proxy) => new();
                public object InitializeRequest(IDerived proxy) => new();
            }

            public interface IBase : IGrainWithStringKey
            {
                CustomCall Call();
            }

            public interface IDerived : IBase { }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("Return-value proxy initializer", diagnostic.GetMessage());
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

    [Fact]
    public async Task GeneratedActivatorConstructor_RejectsAmbiguousGeneratedBaseInitializer()
    {
        var result = await RunGenerator(CommonTypes + """
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }
            public interface ILeft { }
            public interface IRight { }
            public sealed class Both : ILeft, IRight { }

            public abstract class ActivatorSource
            {
                [GeneratedActivatorConstructor]
                protected ActivatorSource(Both value) { }
            }

            public abstract class CustomRequest : ActivatorSource
            {
                protected CustomRequest(ILeft value) : base(null) { }
                protected CustomRequest(IRight value) : base(null) { }
            }

            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("accessible parameterless constructor", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("string[]", "params string[] values")]
    [InlineData("string", "params string[] values")]
    [InlineData("string", "object value, int optional = 0")]
    public async Task GeneratedActivatorConstructor_BindsExactParamsAndOptionalBaseInitializer(
        string activatorParameterType,
        string baseParameters)
    {
        var result = await RunGenerator(CommonTypes + $$"""
            [InvokableBaseType(typeof(GrainReference), typeof(CustomCall), typeof(CustomRequest))]
            public class CustomCall { }

            public abstract class ActivatorSource
            {
                [GeneratedActivatorConstructor]
                protected ActivatorSource({{activatorParameterType}} value) { }
            }

            public abstract class CustomRequest : ActivatorSource
            {
                protected CustomRequest({{baseParameters}}) : base(null) { }
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
    public async Task ReferencedGeneratedActivatorConstructorBinding_IsIndependentOfReferenceOrder()
    {
        var owner = await CompileReference(CommonTypes + """
            namespace Owner
            {
                public class CustomCall { }
                public interface ILeft { }
                public interface IRight { }
                public sealed class Both : ILeft, IRight { }

                public abstract class ActivatorSource
                {
                    [GeneratedActivatorConstructor]
                    protected ActivatorSource(Both value) { }
                }

                public abstract class CustomRequest : ActivatorSource
                {
                    protected CustomRequest(ILeft value) : base(null) { }
                    protected CustomRequest(IRight value) : base(null) { }
                }
            }
            """, "ConstructorOwner");
        var adapter = await CompileReference(CommonTypes + """
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall),
                typeof(Owner.CustomRequest))]
            """, "ConstructorAdapter", owner);
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
    public async Task AssemblyRegistration_CannotReplaceProxyDefaultOrContaminateEffectiveMappings()
    {
        const string registration = """
            [assembly: Orleans.InvokableBaseType(
                typeof(Orleans.Runtime.GrainReference),
                typeof(System.Threading.Tasks.Task),
                typeof(CustomRequest))]
            """;
        var source = CommonTypes + """
            using System.Threading.Tasks;

            public abstract class CustomRequest { }

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ICustomGrain : IGrainWithStringKey
            {
                Task Call();
            }
            """;
        var validCompilation = await TestCompilationHelper.CreateCompilation(source, "CustomReturnTypes");
        var invalidCompilation = validCompilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(registration));
        var invalidInterface = invalidCompilation.GetTypeByMetadataName("ICustomGrain")!;
        var taskType = invalidCompilation.GetTypeByMetadataName("System.Threading.Tasks.Task")!;
        var customRequest = invalidCompilation.GetTypeByMetadataName("CustomRequest")!;
        var resolver = new InvokableBaseTypeResolver(invalidCompilation);

        var mappings = resolver.GetMappingsForProxy(invalidCompilation.GetTypeByMetadataName("Orleans.Runtime.GrainReference")!);
        var taskMapping = Assert.Single(mappings, mapping => SymbolEqualityComparer.Default.Equals(mapping.ReturnType, taskType));
        Assert.Equal("Orleans.Runtime.TaskRequest", taskMapping.InvokableBaseType.ToDisplayString());
        Assert.DoesNotContain(mappings, mapping => SymbolEqualityComparer.Default.Equals(mapping.InvokableBaseType, customRequest));

        var generationContext = new ProxyGenerationContext(invalidCompilation, new CodeGeneratorOptions());
        Assert.True(generationContext.TryGetProxyBaseDescription(invalidInterface, out var proxyBase));
        Assert.Equal(
            "Orleans.Runtime.TaskRequest",
            Assert.Single(proxyBase.InvokableBaseTypes, pair => SymbolEqualityComparer.Default.Equals(pair.Key, taskType))
                .Value.ToDisplayString());

        var validModel = ProxyInterfaceModelExtractor.ExtractProxyInterfaceModel(
            validCompilation.GetTypeByMetadataName("ICustomGrain")!,
            validCompilation,
            CancellationToken.None);
        var invalidModel = ProxyInterfaceModelExtractor.ExtractProxyInterfaceModel(
            invalidInterface,
            invalidCompilation,
            CancellationToken.None);
        Assert.Equal(validModel, invalidModel);

        var result = RunGenerator(invalidCompilation);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, diagnostic.Id);
        Assert.Contains("cannot replace proxy default", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AssemblyReplacementFiltering_IsDeterministicAndPreservesValidAdapters()
    {
        var owner = await CompileReference("""
            namespace Owner;
            public class CustomCall { }
            """, "ReturnTypeOwner");
        var invalidAdapter = await CompileReference(CommonTypes + """
            using System.Threading.Tasks;

            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Task),
                typeof(InvalidAdapter.CustomTaskRequest))]

            namespace InvalidAdapter
            {
                public abstract class CustomTaskRequest { }
            }
            """, "InvalidAdapter", owner);
        var validAdapter = await CompileReference(CommonTypes + """
            [assembly: InvokableBaseType(
                typeof(GrainReference),
                typeof(Owner.CustomCall),
                typeof(ValidAdapter.CustomRequest))]

            namespace ValidAdapter
            {
                public abstract class CustomRequest { }
            }
            """, "ValidAdapter", owner);
        var source = CommonTypes + """
            using System.Threading.Tasks;

            public interface ICustomGrain : IGrainWithStringKey
            {
                Task Call();
                Owner.CustomCall CustomCall();
            }
            """;
        var forwardCompilation = await TestCompilationHelper.CreateCompilation(
            source,
            "CustomReturnTypes",
            owner,
            invalidAdapter,
            validAdapter);
        var reverseCompilation = await TestCompilationHelper.CreateCompilation(
            source,
            "CustomReturnTypes",
            owner,
            validAdapter,
            invalidAdapter);

        var forwardDiagnostic = Assert.Single(RunGenerator(forwardCompilation).Diagnostics);
        var reverseDiagnostic = Assert.Single(RunGenerator(reverseCompilation).Diagnostics);
        Assert.Equal(DiagnosticRuleId.InvalidInvokableBaseTypeMapping, forwardDiagnostic.Id);
        Assert.Equal(forwardDiagnostic.GetMessage(), reverseDiagnostic.GetMessage());
        Assert.Contains("cannot replace proxy default", forwardDiagnostic.GetMessage());

        var forwardMappings = GetEffectiveMappings(forwardCompilation);
        var reverseMappings = GetEffectiveMappings(reverseCompilation);
        Assert.Equal(forwardMappings, reverseMappings);
        Assert.Contains(
            forwardMappings,
            static mapping => mapping == "Owner.CustomCall [ReturnTypeOwner] -> ValidAdapter.CustomRequest [ValidAdapter]");
        var taskAssemblyName = forwardCompilation.GetTypeByMetadataName("System.Threading.Tasks.Task")!.ContainingAssembly.Name;
        Assert.Contains(
            forwardMappings,
            mapping => mapping == $"System.Threading.Tasks.Task [{taskAssemblyName}] -> Orleans.Runtime.TaskRequest [Orleans.Core.Abstractions]");
        Assert.DoesNotContain(forwardMappings, static mapping => mapping.Contains("InvalidAdapter.CustomTaskRequest", StringComparison.Ordinal));

        static string[] GetEffectiveMappings(Compilation compilation)
        {
            var proxyBaseType = compilation.GetTypeByMetadataName("Orleans.Runtime.GrainReference")!;
            return new InvokableBaseTypeResolver(compilation)
                .GetMappingsForProxy(proxyBaseType)
                .Select(static mapping =>
                    $"{mapping.ReturnTypeName} [{mapping.ReturnType.ContainingAssembly.Name}] -> "
                    + $"{mapping.InvokableBaseTypeName} [{mapping.InvokableBaseType.ContainingAssembly.Name}]")
                .ToArray();
        }
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

    [Theory]
    [InlineData("ProxyA", "ProxyB", false)]
    [InlineData("ProxyB", "ProxyA", true)]
    public async Task DirectSerializerAttributes_UseGeneratorSelectedProxyBase(
        string firstProxy,
        string secondProxy,
        bool expectDiagnostic)
    {
        var result = await RunGenerator(CommonTypes + $$"""
            [assembly: InvokableBaseType(typeof(ProxyA), typeof(CustomCall), typeof(CustomRequest))]

            public abstract class ProxyA
            {
                protected System.Threading.Tasks.ValueTask InvokeAsync(Orleans.Serialization.Invocation.IInvokable request) => default;
                protected System.Threading.Tasks.ValueTask<T> InvokeAsync<T>(Orleans.Serialization.Invocation.IInvokable request) => default;
            }
            public abstract class ProxyB
            {
                protected System.Threading.Tasks.ValueTask InvokeAsync(Orleans.Serialization.Invocation.IInvokable request) => default;
                protected System.Threading.Tasks.ValueTask<T> InvokeAsync<T>(Orleans.Serialization.Invocation.IInvokable request) => default;
            }
            public class CustomCall { }
            public abstract class CustomRequest { }

            [GenerateMethodSerializers(typeof({{firstProxy}}))]
            [GenerateMethodSerializers(typeof({{secondProxy}}))]
            public interface ICustomGrain : IGrainWithStringKey
            {
                CustomCall Call();
            }
            """);

        Assert.True(
            expectDiagnostic == result.Diagnostics.Length > 0,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Theory]
    [InlineData("IProxyA", "IProxyB", false)]
    [InlineData("IProxyB", "IProxyA", true)]
    public async Task InheritedSerializerAttributes_UseGeneratorSelectedProxyBase(
        string firstInterface,
        string secondInterface,
        bool expectDiagnostic)
    {
        var result = await RunGenerator(CommonTypes + $$"""
            [assembly: InvokableBaseType(typeof(ProxyA), typeof(CustomCall), typeof(CustomRequest))]

            public abstract class ProxyA
            {
                protected System.Threading.Tasks.ValueTask InvokeAsync(Orleans.Serialization.Invocation.IInvokable request) => default;
                protected System.Threading.Tasks.ValueTask<T> InvokeAsync<T>(Orleans.Serialization.Invocation.IInvokable request) => default;
            }
            public abstract class ProxyB
            {
                protected System.Threading.Tasks.ValueTask InvokeAsync(Orleans.Serialization.Invocation.IInvokable request) => default;
                protected System.Threading.Tasks.ValueTask<T> InvokeAsync<T>(Orleans.Serialization.Invocation.IInvokable request) => default;
            }
            public class CustomCall { }
            public abstract class CustomRequest { }

            [GenerateMethodSerializers(typeof(ProxyA))]
            public interface IProxyA : IGrainWithStringKey { }

            [GenerateMethodSerializers(typeof(ProxyB))]
            public interface IProxyB : IGrainWithStringKey { }

            public interface ICustomGrain : {{firstInterface}}, {{secondInterface}}
            {
                CustomCall Call();
            }
            """);

        Assert.True(
            expectDiagnostic == result.Diagnostics.Length > 0,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
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
        return RunGenerator(compilation);
    }

    private static GeneratorRunResult RunGenerator(Compilation compilation)
    {
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
