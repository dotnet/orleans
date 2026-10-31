using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Orleans.CodeGenerator.CopierGenerator;
using static Orleans.CodeGenerator.InvokableGenerator;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator;

/// <summary>
/// Generates RPC proxies.
/// </summary>
internal class ProxyGenerator(IGeneratorServices generatorServices, CopierGenerator copierGenerator)
{
    private const string CopyContextPoolMemberName = "CopyContextPool";
    private const string CodecProviderMemberName = "CodecProvider";
    private readonly IGeneratorServices _generatorServices = generatorServices;
    private readonly CopierGenerator _copierGenerator = copierGenerator;

    private LibraryTypes LibraryTypes => _generatorServices.LibraryTypes;

    /// <summary>
    /// Plans the proxy for an interface. Planning enumerates the interface's methods, which discovers their invokables
    /// and compatibility invokers, and reports invalid methods.
    /// </summary>
    public ProxyEmissionPlan CreatePlan(ProxyInterfaceDescription interfaceDescription)
    {
        var generatedClassName = GetSimpleClassName(interfaceDescription);
        var fieldDescriptions = GetFieldDescriptions(interfaceDescription);
        var methods = CreateProxyMethods(fieldDescriptions, interfaceDescription);
        var (constructors, constructorStatements) = CreateConstructors(generatedClassName, fieldDescriptions, interfaceDescription.ProxyBaseType);

        return new ProxyEmissionPlan(
            generatedClassName,
            CodeText.Type(interfaceDescription.ProxyBaseType.ToTypeSyntax()),
            CodeText.Type(interfaceDescription.InterfaceType.ToTypeSyntax()),
            GenericParameterEmissionPlan.Create(interfaceDescription.TypeParameters),
            fieldDescriptions.Select(static field => $"private readonly {CodeText.Type(field.FieldType)} {field.FieldName};").ToImmutableArray(),
            constructors,
            constructorStatements,
            methods);
    }

    internal static void Write(SourceWriter writer, ProxyEmissionPlan plan)
    {
        var declaration = new StringBuilder();
        declaration.Append("internal sealed class ").Append(plan.ClassName);
        GenericParameterEmissionPlan.AppendParameterList(declaration, plan.TypeParameters);
        declaration.Append(" : ").Append(plan.BaseType).Append(", ").Append(plan.InterfaceType);
        writer.WriteLine(GeneratedCodeUtilities.GeneratedCodeAttributeText);
        GenericParameterEmissionPlan.WriteDeclarationLine(writer, declaration, plan.TypeParameters);
        writer.OpenBlock();
        foreach (var field in plan.Fields)
        {
            writer.WriteLine(field);
        }

        foreach (var constructor in plan.Constructors)
        {
            writer.WriteLine(constructor);
            writer.OpenBlock();
            foreach (var statement in plan.ConstructorStatements)
            {
                writer.WriteLine(statement);
            }

            writer.CloseBlock();
        }

        foreach (var method in plan.Methods)
        {
            writer.WriteLine(method.Declaration);
            writer.OpenBlock();
            foreach (var statement in method.Statements)
            {
                writer.WriteLine(statement);
            }

            writer.CloseBlock();
        }

        writer.CloseBlock();
    }

    public static string GetSimpleClassName(ProxyInterfaceDescription interfaceDescription)
        => GetSimpleClassName(interfaceDescription.Name);

    public static string GetSimpleClassName(string name)
        => $"Proxy_{SyntaxGeneration.Identifier.SanitizeIdentifierName(name)}";

    private List<GeneratedFieldDescription> GetFieldDescriptions(
        ProxyInterfaceDescription interfaceDescription)
    {
        var fields = new List<GeneratedFieldDescription>();

        // Add a copier field for any method parameter which does not have a static codec.
        var paramCopiers = interfaceDescription.Methods
            .Where(method => method.MethodTypeParameters.Count == 0)
            .SelectMany(method => method.GeneratedInvokable.Members);
        _copierGenerator.GetCopierFieldDescriptions(paramCopiers, fields);
        return fields;
    }

    private EquatableArray<ProxyMethodEmissionPlan> CreateProxyMethods(
        List<GeneratedFieldDescription> fieldDescriptions,
        ProxyInterfaceDescription interfaceDescription)
    {
        var res = ImmutableArray.CreateBuilder<ProxyMethodEmissionPlan>(interfaceDescription.Methods.Count);
        foreach (var methodDescription in interfaceDescription.Methods)
        {
            var forwardingMethod = interfaceDescription.Methods.FirstOrDefault(
                candidate => IsCancellationCompatibilityOverload(methodDescription, candidate));
            res.Add(CreateProxyMethod(methodDescription, forwardingMethod));
        }

        return res.MoveToImmutable();

        ProxyMethodEmissionPlan CreateProxyMethod(
            ProxyMethodDescription methodDescription,
            ProxyMethodDescription? forwardingMethod)
        {
            var (isAsync, body) = forwardingMethod is null
                ? CreateAsyncProxyMethodBody(fieldDescriptions, methodDescription)
                : (false, CreateCompatibilityForwardingBody(methodDescription, forwardingMethod));
            var method = methodDescription.Method;
            var declaration = new StringBuilder();
            if (isAsync)
            {
                declaration.Append("async ");
            }

            CodeText.AppendType(declaration, method.ReturnType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions));
            declaration.Append(' ');
            CodeText.AppendType(declaration, methodDescription.Method.ContainingType.ToNameSyntax());
            declaration.Append('.').Append(method.Name.EscapeIdentifier());
            if (methodDescription.MethodTypeParameters.Count > 0)
            {
                declaration.Append('<').Append(string.Join(", ", methodDescription.MethodTypeParameters.Select(static tp => tp.Name))).Append('>');
            }

            declaration.Append('(');
            AppendParameters(declaration, method.Parameters, methodDescription.TypeParameterSubstitutions);
            declaration.Append(')');
            return new(declaration.ToString(), body);
        }

        bool IsCancellationCompatibilityOverload(
            ProxyMethodDescription legacyMethod,
            ProxyMethodDescription candidate)
        {
            if (ReferenceEquals(legacyMethod, candidate)
                || !string.Equals(legacyMethod.Method.Name, candidate.Method.Name, StringComparison.Ordinal)
                || candidate.Method.TypeParameters.Length != legacyMethod.Method.TypeParameters.Length
                || !HaveEquivalentMethodConstraints(legacyMethod.Method, candidate.Method)
                || candidate.Method.Parameters.Length != legacyMethod.Method.Parameters.Length + 1
                || !AreEquivalentMethodTypes(legacyMethod.Method.ReturnType, candidate.Method.ReturnType)
                || !SymbolEqualityComparer.Default.Equals(
                    candidate.Method.Parameters[candidate.Method.Parameters.Length - 1].Type,
                    LibraryTypes.CancellationToken)
                || !string.Equals(candidate.MethodId, legacyMethod.GeneratedMethodId, StringComparison.Ordinal)
                    && !string.Equals(candidate.InvokableMethod.ClaimedGeneratedMethodId, legacyMethod.GeneratedMethodId, StringComparison.Ordinal))
            {
                return false;
            }

            if (legacyMethod.InvokableMethod.ProxyBase.IsExtension
                && !SymbolEqualityComparer.Default.Equals(
                    legacyMethod.Method.OriginalDefinition.ContainingType,
                    candidate.Method.OriginalDefinition.ContainingType))
            {
                return false;
            }

            for (var i = 0; i < legacyMethod.Method.Parameters.Length; i++)
            {
                var legacyParameter = legacyMethod.Method.Parameters[i];
                var candidateParameter = candidate.Method.Parameters[i];
                if (legacyParameter.RefKind != candidateParameter.RefKind
                    || !AreEquivalentMethodTypes(legacyParameter.Type, candidateParameter.Type))
                {
                    return false;
                }
            }

            return true;
        }

        static bool HaveEquivalentMethodConstraints(IMethodSymbol legacyMethod, IMethodSymbol candidateMethod)
        {
            for (var i = 0; i < legacyMethod.TypeParameters.Length; i++)
            {
                var legacyParameter = legacyMethod.TypeParameters[i];
                var candidateParameter = candidateMethod.TypeParameters[i];
                if (legacyParameter.HasConstructorConstraint != candidateParameter.HasConstructorConstraint
                    || legacyParameter.HasNotNullConstraint != candidateParameter.HasNotNullConstraint
                    || legacyParameter.HasReferenceTypeConstraint != candidateParameter.HasReferenceTypeConstraint
                    || legacyParameter.ReferenceTypeConstraintNullableAnnotation != candidateParameter.ReferenceTypeConstraintNullableAnnotation
                    || legacyParameter.HasUnmanagedTypeConstraint != candidateParameter.HasUnmanagedTypeConstraint
                    || legacyParameter.HasValueTypeConstraint != candidateParameter.HasValueTypeConstraint
                    || legacyParameter.ConstraintTypes.Length != candidateParameter.ConstraintTypes.Length
                    || !HaveEquivalentConstraintTypes(legacyParameter.ConstraintTypes, candidateParameter.ConstraintTypes))
                {
                    return false;
                }
            }

            return true;
        }

        static bool HaveEquivalentConstraintTypes(
            ImmutableArray<ITypeSymbol> legacyConstraints,
            ImmutableArray<ITypeSymbol> candidateConstraints)
        {
            var matched = new bool[candidateConstraints.Length];
            foreach (var legacyConstraint in legacyConstraints)
            {
                var match = -1;
                for (var i = 0; i < candidateConstraints.Length; i++)
                {
                    if (!matched[i] && AreEquivalentMethodTypes(legacyConstraint, candidateConstraints[i]))
                    {
                        match = i;
                        break;
                    }
                }

                if (match < 0)
                {
                    return false;
                }

                matched[match] = true;
            }

            return true;
        }

        static bool AreEquivalentMethodTypes(ITypeSymbol legacyType, ITypeSymbol candidateType)
        {
            if (legacyType is ITypeParameterSymbol legacyTypeParameter
                && candidateType is ITypeParameterSymbol candidateTypeParameter)
            {
                return legacyTypeParameter.TypeParameterKind == candidateTypeParameter.TypeParameterKind
                    && legacyTypeParameter.Ordinal == candidateTypeParameter.Ordinal
                    && (legacyTypeParameter.TypeParameterKind == TypeParameterKind.Method
                        || SymbolEqualityComparer.Default.Equals(
                            legacyTypeParameter.ContainingSymbol,
                            candidateTypeParameter.ContainingSymbol));
            }

            if (legacyType is IArrayTypeSymbol legacyArray && candidateType is IArrayTypeSymbol candidateArray)
            {
                return legacyArray.Rank == candidateArray.Rank
                    && AreEquivalentMethodTypes(legacyArray.ElementType, candidateArray.ElementType);
            }

            if (legacyType is IPointerTypeSymbol legacyPointer && candidateType is IPointerTypeSymbol candidatePointer)
            {
                return AreEquivalentMethodTypes(legacyPointer.PointedAtType, candidatePointer.PointedAtType);
            }

            if (legacyType is INamedTypeSymbol legacyNamed && candidateType is INamedTypeSymbol candidateNamed)
            {
                return SymbolEqualityComparer.Default.Equals(legacyNamed.OriginalDefinition, candidateNamed.OriginalDefinition)
                    && legacyNamed.TypeArguments.Length == candidateNamed.TypeArguments.Length
                    && legacyNamed.TypeArguments.Zip(candidateNamed.TypeArguments, AreEquivalentMethodTypes).All(static equivalent => equivalent);
            }

            return SymbolEqualityComparer.Default.Equals(legacyType, candidateType);
        }

        static EquatableArray<string> CreateCompatibilityForwardingBody(
            ProxyMethodDescription legacyMethod,
            ProxyMethodDescription cancellationMethod)
        {
            var invocation = new StringBuilder("((");
            CodeText.AppendType(invocation, cancellationMethod.Method.ContainingType.ToTypeSyntax(cancellationMethod.TypeParameterSubstitutions));
            invocation.Append(")this).").Append(cancellationMethod.Method.Name.EscapeIdentifier());
            if (cancellationMethod.MethodTypeParameters.Count > 0)
            {
                invocation.Append('<').Append(string.Join(", ", legacyMethod.MethodTypeParameters.Select(static parameter => parameter.Name))).Append('>');
            }

            invocation.Append('(');
            for (var index = 0; index < legacyMethod.Method.Parameters.Length; index++)
            {
                invocation.Append("arg").Append(index).Append(", ");
            }

            invocation.Append("global::System.Threading.CancellationToken.None)");
            return ImmutableArray.Create(legacyMethod.Method.ReturnsVoid ? $"{invocation};" : $"return {invocation};");
        }
    }

    private (bool IsAsync, EquatableArray<string> Body) CreateAsyncProxyMethodBody(
        List<GeneratedFieldDescription> fieldDescriptions,
        ProxyMethodDescription methodDescription)
    {
        var statements = ImmutableArray.CreateBuilder<string>();
        var invokable = methodDescription.GeneratedInvokable;
        var invokableType = CodeText.Type(invokable.TypeSyntax);
        statements.Add(!invokable.IsEmptyConstructable || invokable.UseActivator
            ? $"var request = this.GetInvokable<{invokableType}>();"
            : $"var request = new {invokableType}();");

        var codecs = fieldDescriptions.OfType<ICopierDescription>()
                .Concat(_generatorServices.LibraryTypes.StaticCopiers)
                .ToList();

        // Set request object fields from method parameters.
        var parameterIndex = 0;
        var parameters = invokable.Members.OfType<MethodParameterFieldDescription>().Select(member => new SerializableMethodMember(member));
        var hasCopyContext = false;
        foreach (var parameter in parameters)
        {
            // Only create a copy context as needed.
            if (!hasCopyContext && !parameter.IsShallowCopyable)
            {
                statements.Add($"using var copyContext = base.{CopyContextPoolMemberName}.GetContext();");
                hasCopyContext = true;
            }

            var value = _copierGenerator.GenerateMemberCopy(fieldDescriptions, codecs, parameter).Copy($"arg{parameterIndex}", "copyContext");
            statements.Add($"request.arg{parameterIndex} = {value};");
            parameterIndex++;
        }

        string? invokeMethodName = default;
        foreach (var attr in methodDescription.Method.GetAttributes())
        {
            if (attr.AttributeClass is { } attributeClass && attributeClass.GetAttributes(LibraryTypes.InvokeMethodNameAttribute, out var attrs))
            {
                foreach (var methodAttr in attrs)
                {
                    invokeMethodName = (string?)methodAttr.ConstructorArguments.First().Value;
                }
            }
        }

        var methodReturnType = methodDescription.Method.ReturnType;
        if (methodReturnType is not INamedTypeSymbol namedMethodReturnType)
        {
            var diagnostic = InvalidRpcMethodReturnTypeDiagnostic.CreateDiagnostic(methodDescription.InvokableMethod);
            throw new OrleansGeneratorDiagnosticAnalysisException(diagnostic);
        }

        string baseInvokeExpression;
        var isVoid = methodReturnType.SpecialType is SpecialType.System_Void;
        if (namedMethodReturnType.TypeArguments.Length == 1)
        {
            // Task<T> / ValueTask<T>
            var resultType = namedMethodReturnType.TypeArguments[0];
            baseInvokeExpression = $"base.{CodeText.Identifier(invokeMethodName ?? "InvokeAsync")}<{CodeText.Type(resultType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions))}>";
        }
        else if (isVoid)
        {
            // void
            baseInvokeExpression = $"base.{CodeText.Identifier(invokeMethodName ?? "Invoke")}";
        }
        else
        {
            // Task / ValueTask
            baseInvokeExpression = $"base.{CodeText.Identifier(invokeMethodName ?? "InvokeAsync")}";
        }

        // C#: base.InvokeAsync<TReturn>(request);
        var invocationExpression = $"{baseInvokeExpression}(request)";

        var rt = namedMethodReturnType.ConstructedFrom;
        bool isAsync;
        if (SymbolEqualityComparer.Default.Equals(rt, LibraryTypes.Task_1) || SymbolEqualityComparer.Default.Equals(methodReturnType, LibraryTypes.Task))
        {
            statements.Add($"return {invocationExpression}.AsTask();");
            isAsync = false;
        }
        else if (SymbolEqualityComparer.Default.Equals(rt, LibraryTypes.ValueTask_1) || SymbolEqualityComparer.Default.Equals(methodReturnType, LibraryTypes.ValueTask))
        {
            statements.Add($"return {invocationExpression};");
            isAsync = false;
        }
        else if (invokable.ReturnValueInitializerMethod is { } returnValueInitializerMethod)
        {
            statements.Add($"return request.{CodeText.Identifier(returnValueInitializerMethod)}(this);");
            isAsync = false;
        }
        else if (isVoid)
        {
            statements.Add($"{invocationExpression};");
            isAsync = false;
        }
        else if (rt.Arity == 0)
        {
            statements.Add($"await {invocationExpression};");
            isAsync = true;
        }
        else
        {
            statements.Add($"return await {invocationExpression};");
            isAsync = true;
        }

        return (isAsync, statements.ToImmutable());
    }

    private static (EquatableArray<string> Declarations, EquatableArray<string> Statements) CreateConstructors(
        string simpleClassName,
        List<GeneratedFieldDescription> fieldDescriptions,
        INamedTypeSymbol baseType)
    {
        if (baseType is null)
        {
            return ([], []);
        }

        var statements = ImmutableArray.CreateBuilder<string>();
        foreach (var field in fieldDescriptions)
        {
            var name = CodeText.Identifier(field.FieldName);
            switch (field)
            {
                case GeneratedFieldDescription _ when field.IsInjected:
                    statements.Add($"this.{name} = OrleansGeneratedCodeHelper.UnwrapService(this, {name});");
                    break;
                case CopierFieldDescription:
                    statements.Add($"{name} = {GetServiceExpression(CodeText.Type(field.FieldType), CodecProviderMemberName)};");
                    break;
            }
        }

        var declarations = ImmutableArray.CreateBuilder<string>();
        foreach (var member in baseType.GetMembers())
        {
            if (member is not IMethodSymbol { MethodKind: MethodKind.Constructor } method
                || method.DeclaredAccessibility == Accessibility.Private)
            {
                continue;
            }

            var declaration = new StringBuilder();
            switch (method.DeclaredAccessibility)
            {
                case Accessibility.Public:
                case Accessibility.Protected:
                    declaration.Append("public ");
                    break;
                case Accessibility.Internal:
                case Accessibility.ProtectedOrInternal:
                case Accessibility.ProtectedAndInternal:
                    declaration.Append("internal ");
                    break;
            }

            declaration.Append(simpleClassName).Append('(');
            AppendParameters(declaration, method.Parameters, typeParameterSubstitutions: null);
            declaration.Append(") : base(");
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                if (index > 0)
                {
                    declaration.Append(", ");
                }

                declaration.Append(method.Parameters[index].RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    _ => string.Empty,
                }).Append("arg").Append(index);
            }

            declarations.Add(declaration.Append(')').ToString());
        }

        return (declarations.ToImmutable(), statements.ToImmutable());
    }

    private static void AppendParameters(StringBuilder builder, ImmutableArray<IParameterSymbol> parameters, Dictionary<ITypeParameterSymbol, string>? typeParameterSubstitutions)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var parameter = parameters[index];
            builder.Append(parameter.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                _ => string.Empty,
            });
            CodeText.AppendType(builder, parameter.Type.ToTypeSyntax(typeParameterSubstitutions));
            builder.Append(" arg").Append(index);
        }
    }
}
