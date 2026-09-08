using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Orleans.CodeGenerator.CopierGenerator;
using static Orleans.CodeGenerator.InvokableGenerator;
using static Orleans.CodeGenerator.SerializerGenerator;

namespace Orleans.CodeGenerator;

/// <summary>
/// Generates RPC stub objects called invokers.
/// </summary>
internal class ProxyGenerator(IGeneratorServices generatorServices, CopierGenerator copierGenerator)
{
    private const string CopyContextPoolMemberName = "CopyContextPool";
    private const string CodecProviderMemberName = "CodecProvider";
    private readonly IGeneratorServices _generatorServices = generatorServices;
    private readonly CopierGenerator _copierGenerator = copierGenerator;

    private LibraryTypes LibraryTypes => _generatorServices.LibraryTypes;

    public (ClassDeclarationSyntax, GeneratedProxyDescription) Generate(ProxyInterfaceDescription interfaceDescription)
    {
        var generatedClassName = GetSimpleClassName(interfaceDescription);

        var fieldDescriptions = GetFieldDescriptions(interfaceDescription);
        var fieldDeclarations = GetFieldDeclarations(fieldDescriptions);
        var activators = GetActivators(interfaceDescription);
        var activatorFields = GetActivatorFieldDeclarations(activators);
        var proxyMethods = CreateProxyMethods(fieldDescriptions, interfaceDescription, activators);

        var ctors = GenerateConstructors(generatedClassName, fieldDescriptions, interfaceDescription, activators);

        var classDeclaration = ClassDeclaration(generatedClassName)
            .AddBaseListTypes(
                SimpleBaseType(interfaceDescription.ProxyBaseType.ToTypeSyntax()),
                SimpleBaseType(interfaceDescription.InterfaceType.ToTypeSyntax()))
            .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.SealedKeyword))
            .AddAttributeLists(GeneratedCodeUtilities.GetGeneratedCodeAttributes())
            .AddMembers(fieldDeclarations)
            .AddMembers(activatorFields)
            .AddMembers(ctors)
            .AddMembers(proxyMethods);

        var typeParameters = interfaceDescription.TypeParameters;
        if (typeParameters.Count > 0)
        {
            classDeclaration = SyntaxFactoryUtility.AddGenericTypeParameters(classDeclaration, typeParameters);
        }

        return (classDeclaration, new GeneratedProxyDescription(interfaceDescription, generatedClassName));
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

    private static MemberDeclarationSyntax[] GetFieldDeclarations(List<GeneratedFieldDescription> fieldDescriptions)
    {
        return [.. fieldDescriptions.Select(GetFieldDeclaration)];

        static MemberDeclarationSyntax GetFieldDeclaration(GeneratedFieldDescription description)
        {
            return FieldDeclaration(VariableDeclaration(description.FieldType, SingletonSeparatedList(VariableDeclarator(description.FieldName))))
                .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
        }
    }

    private MemberDeclarationSyntax[] CreateProxyMethods(
        List<GeneratedFieldDescription> fieldDescriptions,
        ProxyInterfaceDescription interfaceDescription,
        List<ActivatorDescription> activators)
    {
        var res = new List<MemberDeclarationSyntax>();
        foreach (var methodDescription in interfaceDescription.Methods)
        {
            var forwardingMethod = interfaceDescription.Methods.FirstOrDefault(
                candidate => IsCancellationCompatibilityOverload(methodDescription, candidate));
            res.Add(CreateProxyMethod(methodDescription, forwardingMethod));
        }
        return [.. res];

        MethodDeclarationSyntax CreateProxyMethod(
            ProxyMethodDescription methodDescription,
            ProxyMethodDescription? forwardingMethod)
        {
            var (isAsync, body) = forwardingMethod is null
                ? CreateAsyncProxyMethodBody(fieldDescriptions, methodDescription, activators)
                : (false, CreateCompatibilityForwardingBody(methodDescription, forwardingMethod));
            var method = methodDescription.Method;
            var declaration = MethodDeclaration(method.ReturnType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions), method.Name.EscapeIdentifier())
                .AddParameterListParameters([.. method.Parameters.Select((p, i) => GetParameterSyntax(i, p, methodDescription.TypeParameterSubstitutions))])
                .WithBody(body);

            if (isAsync)
            {
                declaration = declaration.WithModifiers(TokenList(Token(SyntaxKind.AsyncKeyword)));
            }

            var explicitInterfaceSpecifier = ExplicitInterfaceSpecifier(methodDescription.Method.ContainingType.ToNameSyntax());
            declaration = declaration.WithExplicitInterfaceSpecifier(explicitInterfaceSpecifier);

            if (methodDescription.MethodTypeParameters.Count > 0)
            {
                declaration = declaration.WithTypeParameterList(
                    TypeParameterList(SeparatedList(methodDescription.MethodTypeParameters.Select(tp => TypeParameter(tp.Name)))));
            }

            return declaration;
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

        static BlockSyntax CreateCompatibilityForwardingBody(
            ProxyMethodDescription legacyMethod,
            ProxyMethodDescription cancellationMethod)
        {
            SimpleNameSyntax methodName = cancellationMethod.MethodTypeParameters.Count > 0
                ? GenericName(
                    Identifier(cancellationMethod.Method.Name.EscapeIdentifier()),
                    TypeArgumentList(SeparatedList<TypeSyntax>(
                        legacyMethod.MethodTypeParameters.Select(static parameter => (TypeSyntax)IdentifierName(parameter.Name)))))
                : IdentifierName(cancellationMethod.Method.Name.EscapeIdentifier());
            var target = ParenthesizedExpression(
                CastExpression(
                    cancellationMethod.Method.ContainingType.ToTypeSyntax(cancellationMethod.TypeParameterSubstitutions),
                    ThisExpression()));
            var arguments = legacyMethod.Method.Parameters
                .Select((_, index) => Argument(IdentifierName($"arg{index}")))
                .Append(Argument(ParseExpression("global::System.Threading.CancellationToken.None")));
            var invocation = InvocationExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, target, methodName),
                ArgumentList(SeparatedList(arguments)));

            return legacyMethod.Method.ReturnsVoid
                ? Block(ExpressionStatement(invocation))
                : Block(ReturnStatement(invocation));
        }
    }

    private (bool IsAsync, BlockSyntax body) CreateAsyncProxyMethodBody(
        List<GeneratedFieldDescription> fieldDescriptions,
        ProxyMethodDescription methodDescription,
        List<ActivatorDescription> activators)
    {
        var statements = new List<StatementSyntax>();
        var requestVar = IdentifierName("request");
        var methodSymbol = methodDescription.Method;
        var invokable = methodDescription.GeneratedInvokable;
        ExpressionSyntax createRequestExpr = TryGetActivatorFieldName(activators, methodDescription, out var activatorFieldName) switch
        {
            true => InvocationExpression(IdentifierName(activatorFieldName).Member("Create")),
            _ => (!invokable.IsEmptyConstructable || invokable.UseActivator) switch
            {
                true => InvocationExpression(ThisExpression().Member("GetInvokable", invokable.TypeSyntax))
                    .WithArgumentList(ArgumentList(SeparatedList<ArgumentSyntax>())),
                _ => ObjectCreationExpression(invokable.TypeSyntax).WithArgumentList(ArgumentList())
            }
        };

        statements.Add(
            LocalDeclarationStatement(
                VariableDeclaration(
                    ParseTypeName("var"),
                    SingletonSeparatedList(
                        VariableDeclarator(
                                Identifier("request"))
                            .WithInitializer(
                                EqualsValueClause(createRequestExpr))))));

        var codecs = fieldDescriptions.OfType<ICopierDescription>()
                .Concat(_generatorServices.LibraryTypes.StaticCopiers)
                .ToList();

        // Set request object fields from method parameters.
        var parameterIndex = 0;
        var parameters = invokable.Members.OfType<MethodParameterFieldDescription>().Select(member => new SerializableMethodMember(member));
        ExpressionSyntax copyContextPool = BaseExpression().Member(CopyContextPoolMemberName);
        ExpressionSyntax copyContextVariable = IdentifierName("copyContext");
        var hasCopyContext = false;
        foreach (var parameter in parameters)
        {
            // Only create a copy context as needed.
            if (!hasCopyContext && !parameter.IsShallowCopyable)
            {
                // C#: using var copyContext = base.CopyContext.GetContext();
                statements.Add(
                        LocalDeclarationStatement(
                            VariableDeclaration(
                                ParseTypeName("var"),
                                SingletonSeparatedList(
                                    VariableDeclarator(Identifier("copyContext")).WithInitializer(
                                        EqualsValueClause(InvocationExpression(
                                                copyContextPool.Member("GetContext"),
                                                ArgumentList())))))).WithUsingKeyword(Token(SyntaxKind.UsingKeyword)));
                hasCopyContext = true;
            }

            var valueExpression = _copierGenerator.GenerateMemberCopy(
                fieldDescriptions,
                IdentifierName($"arg{parameterIndex}"),
                copyContextVariable,
                codecs,
                parameter);

            statements.Add(
                ExpressionStatement(
                    AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        requestVar.Member($"arg{parameterIndex}"),
                        valueExpression)));

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

        ExpressionSyntax baseInvokeExpression;
        var isVoid = methodReturnType.SpecialType is SpecialType.System_Void;
        if (namedMethodReturnType.TypeArguments.Length == 1)
        {
            // Task<T> / ValueTask<T>
            var resultType = namedMethodReturnType.TypeArguments[0];
            baseInvokeExpression = BaseExpression().Member(
                invokeMethodName ?? "InvokeAsync",
                resultType.ToTypeSyntax(methodDescription.TypeParameterSubstitutions));
        }
        else if (isVoid)
        {
            // void
            baseInvokeExpression = BaseExpression().Member(invokeMethodName ?? "Invoke");
        }
        else
        {
            // Task / ValueTask
            baseInvokeExpression = BaseExpression().Member(invokeMethodName ?? "InvokeAsync");
        }

        // C#: base.InvokeAsync<TReturn>(request);
        var invocationExpression =
                     InvocationExpression(
                         baseInvokeExpression,
                         ArgumentList(SeparatedList([Argument(requestVar)])));

        var rt = namedMethodReturnType.ConstructedFrom;
        bool isAsync;
        if (SymbolEqualityComparer.Default.Equals(rt, LibraryTypes.Task_1) || SymbolEqualityComparer.Default.Equals(methodReturnType, LibraryTypes.Task))
        {
            // C#: return <invocation>.AsTask()
            statements.Add(ReturnStatement(InvocationExpression(invocationExpression.Member("AsTask"), ArgumentList())));
            isAsync = false;
        }
        else if (SymbolEqualityComparer.Default.Equals(rt, LibraryTypes.ValueTask_1) || SymbolEqualityComparer.Default.Equals(methodReturnType, LibraryTypes.ValueTask))
        {
            // ValueTask<T> / ValueTask
            // C#: return <invocation>
            statements.Add(ReturnStatement(invocationExpression));
            isAsync = false;
        }
        else if (invokable.ReturnValueInitializerMethod is { } returnValueInitializerMethod)
        {
            // C#: return request.<returnValueInitializerMethod>(this);
            statements.Add(ReturnStatement(InvocationExpression(requestVar.Member(returnValueInitializerMethod), ArgumentList(SingletonSeparatedList(Argument(ThisExpression()))))));
            isAsync = false;
        }
        else if (isVoid)
        {
            // C#: <invocation>
            statements.Add(ExpressionStatement(invocationExpression));
            isAsync = false;
        }
        else if (rt.Arity == 0)
        {
            // C#: await <invocation>
            statements.Add(ExpressionStatement(AwaitExpression(invocationExpression)));
            isAsync = true;
        }
        else
        {
            // C#: return await <invocation>
            statements.Add(ReturnStatement(AwaitExpression(invocationExpression)));
            isAsync = true;
        }

        return (isAsync, Block(statements));
    }

    private MemberDeclarationSyntax[] GenerateConstructors(
        string simpleClassName,
        List<GeneratedFieldDescription> fieldDescriptions,
        ProxyInterfaceDescription interfaceDescription,
        List<ActivatorDescription> activators)
    {
        var baseType = interfaceDescription.ProxyBaseType;
        if (baseType is null)
        {
            return [];
        }

        var bodyStatements = GetBodyStatements();
        var res = new List<MemberDeclarationSyntax>();
        foreach (var member in baseType.GetMembers())
        {
            if (member is not IMethodSymbol method)
            {
                continue;
            }

            if (method.MethodKind != MethodKind.Constructor)
            {
                continue;
            }

            if (method.DeclaredAccessibility == Accessibility.Private)
            {
                continue;
            }

            res.Add(CreateConstructor(method));
        }
        return [.. res];

        ConstructorDeclarationSyntax CreateConstructor(IMethodSymbol baseConstructor)
        {
            return ConstructorDeclaration(simpleClassName)
                .AddParameterListParameters([.. baseConstructor.Parameters.Select((p, i) => GetParameterSyntax(i, p, typeParameterSubstitutions: null))])
                .WithModifiers(TokenList(GetModifiers(baseConstructor)))
                .WithInitializer(
                    ConstructorInitializer(
                        SyntaxKind.BaseConstructorInitializer,
                        ArgumentList(
                            SeparatedList(baseConstructor.Parameters.Select(GetBaseInitializerArgument)))))
                .WithBody(Block(bodyStatements));
        }

        static SyntaxToken[] GetModifiers(IMethodSymbol method)
        {
            switch (method.DeclaredAccessibility)
            {
                case Accessibility.Public:
                case Accessibility.Protected:
                    return [Token(SyntaxKind.PublicKeyword)];
                case Accessibility.Internal:
                case Accessibility.ProtectedOrInternal:
                case Accessibility.ProtectedAndInternal:
                    return [Token(SyntaxKind.InternalKeyword)];
                default:
                    return [];
            }
        }

        static ArgumentSyntax GetBaseInitializerArgument(IParameterSymbol parameter, int index)
        {
            var name = $"arg{index}";
            var result = Argument(IdentifierName(name));
            switch (parameter.RefKind)
            {
                case RefKind.None:
                    break;
                case RefKind.Ref:
                    result = result.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                    break;
                case RefKind.Out:
                    result = result.WithRefOrOutKeyword(Token(SyntaxKind.OutKeyword));
                    break;
                default:
                    break;
            }

            return result;
        }

        List<StatementSyntax> GetBodyStatements()
        {
            var res = new List<StatementSyntax>();
            foreach (var field in fieldDescriptions)
            {
                switch (field)
                {
                    case GeneratedFieldDescription _ when field.IsInjected:
                        res.Add(ExpressionStatement(
                            AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                ThisExpression().Member(field.FieldName.ToIdentifierName()),
                                Unwrapped(field.FieldName.ToIdentifierName()))));
                        break;
                    case CopierFieldDescription codec:
                        {
                            res.Add(ExpressionStatement(
                                AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    field.FieldName.ToIdentifierName(),
                                    GetService(field.FieldType))));
                        }
                        break;
                }
            }

            foreach (var activator in activators)
            {
                res.Add(
                    ExpressionStatement(
                        AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            IdentifierName(activator.FieldName),
                            InvocationExpression(
                                IdentifierName(CodecProviderMemberName).Member("GetActivator", activator.Method.GeneratedInvokable.TypeSyntax)))));
            }

            return res;

            static ExpressionSyntax Unwrapped(ExpressionSyntax expr)
            {
                return InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("OrleansGeneratedCodeHelper"), IdentifierName("UnwrapService")),
                    ArgumentList(SeparatedList([Argument(ThisExpression()), Argument(expr)])));
            }

            static ExpressionSyntax GetService(TypeSyntax type)
            {
                return InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("OrleansGeneratedCodeHelper"), GenericName(Identifier("GetService"), TypeArgumentList(SingletonSeparatedList(type)))),
                    ArgumentList(SeparatedList([Argument(ThisExpression()), Argument(IdentifierName(CodecProviderMemberName))])));
            }
        }
    }

    private List<ActivatorDescription> GetActivators(ProxyInterfaceDescription interfaceDescription)
    {
        var result = new List<ActivatorDescription>();
        foreach (var method in interfaceDescription.Methods)
        {
            if (method.MethodTypeParameters.Count == 0
                && method.GeneratedInvokable.UsesInvokablePool)
            {
                result.Add(new(method));
            }
        }

        return result;
    }

    private MemberDeclarationSyntax[] GetActivatorFieldDeclarations(List<ActivatorDescription> activators)
    {
        var result = new List<MemberDeclarationSyntax>();
        foreach (var activator in activators)
        {
            result.Add(
                FieldDeclaration(
                    VariableDeclaration(
                        LibraryTypes.IActivator_1.ToTypeSyntax(activator.Method.GeneratedInvokable.TypeSyntax),
                        SingletonSeparatedList(VariableDeclarator(activator.FieldName))))
                .AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword)));
        }

        return [.. result];
    }

    private static bool TryGetActivatorFieldName(
        List<ActivatorDescription> activators,
        ProxyMethodDescription method,
        out string fieldName)
    {
        foreach (var activator in activators)
        {
            if (ReferenceEquals(activator.Method, method))
            {
                fieldName = activator.FieldName;
                return true;
            }
        }

        fieldName = default!;
        return false;
    }

    private sealed class ActivatorDescription(ProxyMethodDescription method)
    {
        public ProxyMethodDescription Method { get; } = method;
        public string FieldName { get; } = $"_activator_{method.GeneratedMethodId}_{GeneratedSourceOutput.CreateStableHash(method.GeneratedInvokable.TypeSyntax.ToString())}";
    }

    private static ParameterSyntax GetParameterSyntax(int index, IParameterSymbol parameter, Dictionary<ITypeParameterSymbol, string>? typeParameterSubstitutions)
    {
        var result = Parameter(Identifier($"arg{index}")).WithType(parameter.Type.ToTypeSyntax(typeParameterSubstitutions));
        switch (parameter.RefKind)
        {
            case RefKind.None:
                break;
            case RefKind.Ref:
                result = result.WithModifiers(TokenList(Token(SyntaxKind.RefKeyword)));
                break;
            case RefKind.Out:
                result = result.WithModifiers(TokenList(Token(SyntaxKind.OutKeyword)));
                break;
            case RefKind.In:
                result = result.WithModifiers(TokenList(Token(SyntaxKind.InKeyword)));
                break;
            default:
                break;
        }

        return result;
    }
}
