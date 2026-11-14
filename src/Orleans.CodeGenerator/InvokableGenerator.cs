using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator;

/// <summary>
/// Generates RPC stub objects called invokers.
/// </summary>
internal class InvokableGenerator(ProxyGenerationContext generationContext)
{
    private readonly ProxyGenerationContext _generationContext = generationContext;

    private LibraryTypes LibraryTypes => _generationContext.LibraryTypes;

    public GeneratedInvokableDescription Generate(InvokableMethodDescription invokableMethodInfo)
    {
        var method = invokableMethodInfo.Method;
        var generatedClassName = GetSimpleClassName(invokableMethodInfo);

        var baseClassType = GetBaseClassType(invokableMethodInfo);
        var fieldDescriptions = GetFieldDescriptions(invokableMethodInfo);
        var (constructor, ctorArgs) = CreateConstructorPlan(invokableMethodInfo, baseClassType);
        var accessibility = GetAccessibility(method);
        var compoundTypeAliases = GetCompoundTypeAliasAttributeArguments(
            invokableMethodInfo,
            invokableMethodInfo.Key,
            includeGeneratedMethodId: !IsGeneratedMethodIdExplicitlyClaimed(invokableMethodInfo));

        List<INamedTypeSymbol> serializationHooks = new();
        if (baseClassType.GetAttributes(LibraryTypes.SerializationCallbacksAttribute, out var hookAttributes))
        {
            foreach (var hookAttribute in hookAttributes)
            {
                var hookType = (INamedTypeSymbol)hookAttribute.ConstructorArguments[0].Value!;
                serializationHooks.Add(hookType);
            }
        }

        var targetField = fieldDescriptions.OfType<TargetFieldDescription>().Single();
        var emissionPlan = CreateEmissionPlan(
            invokableMethodInfo,
            generatedClassName,
            baseClassType,
            fieldDescriptions,
            constructor,
            compoundTypeAliases,
            targetField,
            accessibility == Accessibility.Public);

        string? returnValueInitializerMethod = null;
        if (baseClassType.GetAttribute(LibraryTypes.ReturnValueProxyAttribute) is { ConstructorArguments: { Length: > 0 } attrArgs })
        {
            returnValueInitializerMethod = (string?)attrArgs[0].Value;
        }

        while (baseClassType.HasAttribute(LibraryTypes.SerializerTransparentAttribute))
        {
            baseClassType = baseClassType.BaseType!;
        }

        var invokerDescription = new GeneratedInvokableDescription(
            invokableMethodInfo,
            accessibility,
            generatedClassName,
            GeneratedCodeUtilities.GetGeneratedNamespaceName(invokableMethodInfo.ContainingInterface),
            [.. fieldDescriptions.OfType<IMemberDescription>()],
            serializationHooks,
            baseClassType,
            ctorArgs,
            compoundTypeAliases,
            returnValueInitializerMethod,
            emissionPlan);
        return invokerDescription;

        static Accessibility GetAccessibility(IMethodSymbol methodSymbol)
        {
            Accessibility accessibility = methodSymbol.DeclaredAccessibility;
            var t = methodSymbol.ContainingType;
            while (t is not null)
            {
                if ((int)t.DeclaredAccessibility < (int)accessibility)
                {
                    accessibility = t.DeclaredAccessibility;
                }

                t = t.ContainingType;
            }

            return accessibility;
        }
    }

    private InvokableEmissionPlan CreateEmissionPlan(
        InvokableMethodDescription method,
        string generatedClassName,
        INamedTypeSymbol baseClassType,
        List<InvokerFieldDescription> fieldDescriptions,
        InvokableConstructorEmissionPlan? constructor,
        List<CompoundTypeAliasComponent[]> compoundTypeAliases,
        TargetFieldDescription targetField,
        bool isPublic)
    {
        var substitutions = method.TypeParameterSubstitutions;
        var methodSymbol = method.Method;
        var containingType = CodeText.Type(methodSymbol.ContainingType.ToTypeSyntax(substitutions));
        var cancellationTokenSourceField = method.IsCancellable
            ? fieldDescriptions.First(static field => field is CancellationTokenSourceFieldDescription).FieldName
            : null;
        var cancellationTokenField = method.IsCancellable
            ? fieldDescriptions.First(field => SymbolEqualityComparer.Default.Equals(LibraryTypes.CancellationToken, field.FieldType)).FieldName
            : null;

        var disposeStatements = ImmutableArray.CreateBuilder<string>();
        var arguments = ImmutableArray.CreateBuilder<InvokableArgumentEmissionPlan>();
        foreach (var field in fieldDescriptions)
        {
            if (field is CancellationTokenSourceFieldDescription)
            {
                disposeStatements.Add($"{CodeText.Identifier(field.FieldName)}?.Dispose();");
            }

            if (field.IsInstanceField)
            {
                disposeStatements.Add($"{field.FieldName} = default;");
            }

            if (field is MethodParameterFieldDescription parameter)
            {
                arguments.Add(new(CodeText.Literal(parameter.ParameterOrdinal), parameter.FieldName,
                    CodeText.Type(parameter.FieldType.ToTypeSyntax(substitutions))));
            }
        }

        var invokeTarget = method.MethodTypeParameters.Count > 0
            ? $"{targetField.FieldName}.{methodSymbol.Name}<{string.Join(", ", method.MethodTypeParameters.Select(static p => p.Name))}>"
            : $"{targetField.FieldName}.{CodeText.Identifier(methodSymbol.Name)}";
        var invokeArguments = string.Join(", ", fieldDescriptions.OfType<MethodParameterFieldDescription>()
            .OrderBy(static p => p.ParameterOrdinal)
            .Select(static p => p.FieldName));

        return new InvokableEmissionPlan(
            generatedClassName,
            isPublic,
            CodeText.Type(baseClassType.ToTypeSyntax(substitutions)),
            compoundTypeAliases.Select(GetCompoundTypeAliasAttribute).ToImmutableArray(),
            GenericParameterEmissionPlan.Create(method.AllTypeParameters),
            fieldDescriptions.Select(field => GetFieldDeclaration(method, field)).ToImmutableArray(),
            constructor,
            method.ResponseTimeoutTicks is { } ticks ? CodeText.Literal(ticks) : null,
            LibraryTypes.EmissionTypes.TimeSpan,
            methodSymbol.Parameters.Length,
            CodeText.StringLiteral(methodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            CodeText.StringLiteral(methodSymbol.ContainingType.ToDisplayName(substitutions, includeGlobalSpecifier: false)),
            CodeText.StringLiteral(GetActivityName(method)),
            LibraryTypes.EmissionTypes.SystemType,
            containingType,
            LibraryTypes.EmissionTypes.MethodInfo,
            LibraryTypes.EmissionTypes.TargetHolder,
            CodeText.Type(method.ContainingInterface.ToTypeSyntax()),
            method.Key.ProxyBase.IsExtension,
            cancellationTokenSourceField,
            cancellationTokenField is null ? null : CodeText.Identifier(cancellationTokenField),
            LibraryTypes.EmissionTypes.CancellationToken,
            disposeStatements.ToImmutable(),
            ShouldDisposeBase(baseClassType),
            arguments.ToImmutable(),
            CodeText.Literal(Math.Max(0, methodSymbol.Parameters.Length - 1)),
            CodeText.Type(methodSymbol.ReturnType.ToTypeSyntax(substitutions)),
            invokeTarget,
            invokeArguments,
            OptsOutOfGrainCancellationTokenScan(method, baseClassType));
    }

    private string GetFieldDeclaration(InvokableMethodDescription method, InvokerFieldDescription description)
    {
        if (description is MethodInfoFieldDescription)
        {
            var methodTypeArguments = GetTypesArray(method, method.MethodTypeParameters.Select(p => p.Parameter));
            var parameterTypes = GetTypesArray(method, method.Method.Parameters.Select(p => p.Type));
            return $"private static readonly {LibraryTypes.EmissionTypes.MethodInfo} {description.FieldName} = "
                + $"OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof({CodeText.Type(method.Method.ContainingType.ToTypeSyntax(method.TypeParameterSubstitutions))}), "
                + $"{CodeText.QuotedText(method.Method.Name)}, {methodTypeArguments}, {parameterTypes});";
        }

        var declaration = $"{CodeText.Type(description.FieldType.ToTypeSyntax(method.TypeParameterSubstitutions))} {description.FieldName};";
        return description is MethodParameterFieldDescription ? "public " + declaration : declaration;
    }

    private static string GetTypesArray(InvokableMethodDescription method, IEnumerable<ITypeSymbol> typeSymbols)
    {
        var types = typeSymbols.ToArray();
        if (types.Length == 0)
        {
            return "null";
        }

        var result = new StringBuilder("new[] { ");
        for (var i = 0; i < types.Length; i++)
        {
            if (i > 0)
            {
                result.Append(", ");
            }

            result.Append("typeof(");
            CodeText.AppendType(result, types[i].ToTypeSyntax(method.TypeParameterSubstitutions));
            result.Append(')');
        }

        return result.Append(" }").ToString();
    }

    private static string GetActivityName(InvokableMethodDescription methodDescription)
    {
        // This property is intended to contain a value suitable for use as an OpenTelemetry Span Name for RPC calls.
        // Therefore, the interface name and method name components must not include periods or slashes.
        // In order to avoid that, we omit the namespace from the interface name.
        // See: https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/rpc.md
        var interfaceName = methodDescription.Method.ContainingType.ToDisplayName(methodDescription.TypeParameterSubstitutions, includeGlobalSpecifier: false, includeNamespace: false);
        var methodName = methodDescription.Method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{interfaceName}/{methodName}";
    }

    private static bool ShouldDisposeBase(INamedTypeSymbol baseClassType)
        => baseClassType.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_IDisposable)
            && baseClassType.GetAllMembers<IMethodSymbol>("Dispose").FirstOrDefault(m => !m.IsAbstract && m.DeclaredAccessibility != Accessibility.Private) is { };

    // The runtime scans the arguments of a request for GrainCancellationToken instances unless the request opts out.
    // Opt out when no argument can hold one, so that the runtime does not box value-type arguments on every call.
    // Methods without parameters are skipped since the scan is already free for them.
    private bool OptsOutOfGrainCancellationTokenScan(InvokableMethodDescription method, INamedTypeSymbol baseClassType)
    {
        if (LibraryTypes.GrainCancellationTokenOrDefault is not { } grainCancellationToken
            || LibraryTypes.RequestBaseOrDefault is not { } requestBase
            || method.Method.Parameters.Length == 0
            || !baseClassType.HasBaseType(requestBase)
            || method.Method.Parameters.Any(parameter => CanHoldGrainCancellationToken(parameter.Type)))
        {
            return false;
        }

        return true;

        // A type parameter may be instantiated as GrainCancellationToken, and any type which GrainCancellationToken
        // converts to (such as object or an interface it implements) may hold an instance at runtime.
        bool CanHoldGrainCancellationToken(ITypeSymbol type)
            => type is ITypeParameterSymbol
                || LibraryTypes.Compilation.HasImplicitConversion(grainCancellationToken, type);
    }

    internal static void Write(SourceWriter writer, InvokableEmissionPlan plan)
    {
        writer.WriteLine(GeneratedCodeUtilities.GeneratedCodeAttributeText);
        foreach (var alias in plan.CompoundTypeAliases)
        {
            writer.WriteLine(alias);
        }

        var declaration = new StringBuilder();
        declaration.Append(plan.IsPublic ? "public" : "internal").Append(" sealed class ").Append(plan.ClassName);
        GenericParameterEmissionPlan.AppendParameterList(declaration, plan.TypeParameters);
        declaration.Append(" : ").Append(plan.BaseType);
        GenericParameterEmissionPlan.WriteDeclarationLine(writer, declaration, plan.TypeParameters);
        writer.OpenBlock();
        foreach (var field in plan.Fields)
        {
            writer.WriteLine(field);
        }

        if (plan.Constructor is { } constructor)
        {
            writer.WriteLine($"public {plan.ClassName}({constructor.Parameters}) : base({constructor.BaseArguments})");
            writer.OpenBlock();
            foreach (var statement in constructor.Statements)
            {
                writer.WriteLine(statement);
            }

            writer.CloseBlock();
        }

        if (plan.ResponseTimeoutTicks is { } ticks)
        {
            writer.WriteLine($"private static readonly {plan.TimeSpanType} _responseTimeoutValue = global::System.TimeSpan.FromTicks({ticks});");
            writer.WriteLine($"public override {plan.TimeSpanType}? GetDefaultResponseTimeout() => _responseTimeoutValue;");
        }

        if (plan.ArgumentCount > 0)
        {
            writer.WriteLine($"public override int GetArgumentCount() => {CodeText.Literal(plan.ArgumentCount)};");
        }

        writer.WriteLine($"public override string GetMethodName() => {plan.MethodName};");
        writer.WriteLine($"public override string GetInterfaceName() => {plan.InterfaceName};");
        writer.WriteLine($"public override string GetActivityName() => {plan.ActivityName};");
        writer.WriteLine($"public override {plan.SystemType} GetInterfaceType() => typeof({plan.InterfaceType});");
        writer.WriteLine($"public override {plan.MethodInfoType} GetMethod() => MethodBackingField;");

        var getTarget = plan.IsExtension
            ? $"_target = ({plan.TargetType})holder.GetComponent(typeof({plan.TargetType}))"
            : $"_target = ({plan.TargetType})holder.GetTarget()";
        var setTarget = $"public override void SetTarget({plan.TargetHolderType} holder)";
        if (plan.CancellationTokenSourceField is not { } ctsField)
        {
            writer.WriteLine($"{setTarget} => {getTarget};");
        }
        else
        {
            writer.WriteLine(setTarget);
            writer.OpenBlock();
            writer.WriteLine(getTarget + ";");
            writer.WriteLine($"{ctsField} = new();");
            writer.WriteLine($"{plan.CancellationTokenField} = {ctsField}.Token;");
            writer.CloseBlock();
        }

        writer.WriteLine("public override object GetTarget() => _target;");
        writer.WriteLine("public override void Dispose()");
        writer.OpenBlock();
        foreach (var statement in plan.DisposeStatements)
        {
            writer.WriteLine(statement);
        }

        if (plan.DisposeBase)
        {
            writer.WriteLine("base.Dispose();");
        }

        writer.CloseBlock();

        if (plan.ArgumentCount > 0)
        {
            writer.WriteLine("public override object GetArgument(int index)");
            writer.OpenBlock();
            writer.WriteLine("switch (index)");
            writer.OpenBlock();
            foreach (var argument in plan.Arguments)
            {
                writer.WriteLine($"case {argument.Ordinal}:");
                writer.Indent();
                writer.WriteLine($"return {argument.FieldName};");
                writer.Unindent();
            }

            writer.WriteLine("default:");
            writer.Indent();
            writer.WriteLine($"return OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, {plan.MaxArgumentIndex});");
            writer.Unindent();
            writer.CloseBlock();
            writer.CloseBlock();

            writer.WriteLine("public override void SetArgument(int index, object value)");
            writer.OpenBlock();
            writer.WriteLine("switch (index)");
            writer.OpenBlock();
            foreach (var argument in plan.Arguments)
            {
                writer.WriteLine($"case {argument.Ordinal}:");
                writer.Indent();
                writer.WriteLine($"{argument.FieldName} = ({argument.Type})value;");
                writer.WriteLine("return;");
                writer.Unindent();
            }

            writer.WriteLine("default:");
            writer.Indent();
            writer.WriteLine($"OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, {plan.MaxArgumentIndex});");
            writer.WriteLine("return;");
            writer.Unindent();
            writer.CloseBlock();
            writer.CloseBlock();
        }

        writer.WriteLine($"protected override {plan.ReturnType} InvokeInner() => {plan.InvokeTarget}({plan.InvokeArguments});");
        if (plan.CancellationTokenField is { } ctField)
        {
            writer.WriteLine($"public override {plan.CancellationTokenType} GetCancellationToken() => {ctField};");
            writer.WriteLine("public override bool TryCancel()");
            writer.OpenBlock();
            writer.WriteLine($"if ({plan.CancellationTokenSourceField} is {{ }} cts)");
            writer.OpenBlock();
            writer.WriteLine("cts.Cancel(false);");
            writer.WriteLine("return true;");
            writer.CloseBlock();
            writer.WriteLine("return false;");
            writer.CloseBlock();
            writer.WriteLine("public override bool IsCancellable => true;");
        }

        if (plan.OptOutOfGrainCancellationTokenScan)
        {
            writer.WriteLine("public override bool MayHaveGrainCancellationToken => false;");
        }

        writer.CloseBlock();
    }

    internal string GetCompoundTypeAliasAttribute(CompoundTypeAliasComponent[] argValues)
    {
        var result = new StringBuilder("[");
        CodeText.AppendType(result, LibraryTypes.CompoundTypeAliasAttribute.ToNameSyntax());
        result.Append('(');
        for (var i = 0; i < argValues.Length; i++)
        {
            if (i > 0)
            {
                result.Append(", ");
            }

            switch (argValues[i].Value)
            {
                case string stringValue:
                    result.Append(CodeText.StringLiteral(stringValue));
                    break;
                case ITypeSymbol typeValue:
                    result.Append("typeof(");
                    CodeText.AppendType(result, typeValue.ToOpenTypeSyntax());
                    result.Append(')');
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported value");
            }
        }

        return result.Append(")]").ToString();
    }

    internal bool IsGeneratedMethodIdExplicitlyClaimed(InvokableMethodDescription methodDescription)
    {
        var containingInterface = methodDescription.ContainingInterface;
        foreach (var method in containingInterface.GetMembers().OfType<IMethodSymbol>()
            .Concat(containingInterface.AllInterfaces.SelectMany(static interfaceType => interfaceType.GetMembers().OfType<IMethodSymbol>())))
        {
            if (SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, methodDescription.Method.OriginalDefinition))
            {
                continue;
            }

            if (methodDescription.ProxyBase.IsExtension
                && !SymbolEqualityComparer.Default.Equals(
                    method.OriginalDefinition.ContainingType,
                    methodDescription.Method.OriginalDefinition.ContainingType))
            {
                continue;
            }

            var explicitMethodId = GeneratedCodeUtilities.GetMethodId(LibraryTypes, method.OriginalDefinition);
            var claimedGeneratedMethodId = GeneratedCodeUtilities.GetClaimedGeneratedMethodId(
                LibraryTypes,
                method.OriginalDefinition,
                containingInterface,
                methodDescription.ProxyBase.IsExtension);
            if (string.Equals(explicitMethodId, methodDescription.GeneratedMethodId, StringComparison.Ordinal)
                || string.Equals(claimedGeneratedMethodId, methodDescription.GeneratedMethodId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static List<CompoundTypeAliasComponent[]> GetCompoundTypeAliasAttributeArguments(
        InvokableMethodDescription methodDescription,
        InvokableMethodId invokableId,
        bool includeGeneratedMethodId = true)
    {
        var result = new List<CompoundTypeAliasComponent[]>(1);
        var containingInterface = methodDescription.ContainingInterface;
        if (methodDescription.HasAlias)
        {
            result.Add(GetCompoundTypeAliasComponents(
                invokableId,
                containingInterface,
                methodDescription.ClaimedGeneratedMethodId ?? methodDescription.MethodId));
        }
        else if (includeGeneratedMethodId)
        {
            result.Add(GetCompoundTypeAliasComponents(invokableId, containingInterface, methodDescription.GeneratedMethodId));
        }

        return result;
    }

    public static CompoundTypeAliasComponent[] GetCompoundTypeAliasComponents(
        InvokableMethodId invokableId,
        INamedTypeSymbol containingInterface,
        string methodId)
    {
        var proxyBase = invokableId.ProxyBase;
        var proxyBaseComponents = proxyBase.CompositeAliasComponents;
        var extensionArgCount = proxyBase.IsExtension ? 1 : 0;
        var alias = new CompoundTypeAliasComponent[1 + proxyBaseComponents.Length + extensionArgCount + 2];
        alias[0] = new("inv");
        for (var i = 0; i < proxyBaseComponents.Length; i++)
        {
            alias[i + 1] = proxyBaseComponents[i];
        }

        alias[1 + proxyBaseComponents.Length] = new(containingInterface);

        // For grain extensions, also explicitly include the method's containing type.
        // This is to distinguish between different extension methods with the same id (eg, alias) but different containing types.
        if (proxyBase.IsExtension)
        {
            alias[1 + proxyBaseComponents.Length + 1] = new(invokableId.Method.ContainingType);
        }

        alias[1 + proxyBaseComponents.Length + extensionArgCount + 1] = new(methodId);
        return alias;
    }

    private INamedTypeSymbol GetBaseClassType(InvokableMethodDescription method)
    {
        if (_generationContext.InvokableBaseTypeResolver.TryResolveBaseType(
            method.ProxyBase.ProxyBaseType,
            method.Method,
            out var baseClassType,
            out var resolverDiagnostic))
        {
            return baseClassType!;
        }

        if (resolverDiagnostic is { Kind: ResolverDiagnosticKind.InvalidMapping })
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(
                InvokableBaseTypeMappingDiagnostic.CreateDiagnostic(resolverDiagnostic));
        }

        throw new OrleansGeneratorDiagnosticAnalysisException(InvalidRpcMethodReturnTypeDiagnostic.CreateDiagnostic(method));
    }

    public static string GetSimpleClassName(InvokableMethodDescription method)
    {
        var genericArity = method.AllTypeParameters.Count;
        var typeArgs = genericArity > 0 ? "_" + genericArity : string.Empty;
        var proxyKey = method.ProxyBase.Key.GeneratedClassNameComponent;
        return $"Invokable_{method.ContainingInterface.Name}_{proxyKey}_{method.GeneratedMethodId}{typeArgs}";
    }

    private (InvokableConstructorEmissionPlan? Constructor, List<TypeSyntax> ConstructorArguments) CreateConstructorPlan(
        InvokableMethodDescription method,
        INamedTypeSymbol baseClassType)
    {
        var parameters = new List<string>();
        var baseArguments = new List<string>();
        var statements = ImmutableArray.CreateBuilder<string>();

        List<TypeSyntax> constructorArgumentTypes = new();
        foreach (var constructor in baseClassType.GetAllMembers<IMethodSymbol>())
        {
            if (constructor.MethodKind != MethodKind.Constructor || constructor.DeclaredAccessibility == Accessibility.Private || constructor.IsImplicitlyDeclared)
            {
                continue;
            }

            if (constructor.HasAttribute(LibraryTypes.GeneratedActivatorConstructorAttribute))
            {
                var index = 0;
                foreach (var parameter in constructor.Parameters)
                {
                    var identifier = $"base{index}";

                    var argumentType = parameter.Type.ToTypeSyntax(method.TypeParameterSubstitutions);
                    constructorArgumentTypes.Add(argumentType);
                    parameters.Add($"{CodeText.Type(argumentType)} {identifier}");
                    baseArguments.Add(identifier);
                    index++;
                }
                break;
            }
        }

        foreach (var (methodName, methodArgument) in method.CustomInitializerMethods)
        {
            // Attribute arguments are spelled by Roslyn's literal and enum formatting.
            var argument = methodArgument.ToExpression().NormalizeWhitespace().ToFullString();
            statements.Add($"{methodName}({argument});");
        }

        if (statements.Count == 0 && parameters.Count == 0)
            return (null, constructorArgumentTypes);

        return (new InvokableConstructorEmissionPlan(string.Join(", ", parameters), string.Join(", ", baseArguments), statements.ToImmutable()),
            constructorArgumentTypes);
    }

    private List<InvokerFieldDescription> GetFieldDescriptions(InvokableMethodDescription method)
    {
        var fields = new List<InvokerFieldDescription>();
        var assignedFieldIds = new Dictionary<uint, IParameterSymbol>();
        uint serializedParameterOrdinal = 0;

        foreach (var parameter in method.Method.Parameters)
        {
            var isSerializable = !SymbolEqualityComparer.Default.Equals(LibraryTypes.CancellationToken, parameter.Type);
            var explicitFieldId = GeneratedCodeUtilities.GetId(LibraryTypes, parameter);
            if (!isSerializable)
            {
                if (explicitFieldId.HasValue)
                {
                    throw new OrleansGeneratorDiagnosticAnalysisException(
                        InvalidRpcParameterIdDiagnostic.CreateCancellationTokenDiagnostic(parameter));
                }

                fields.Add(new MethodParameterFieldDescription(
                    LibraryTypes,
                    parameter,
                    $"arg{parameter.Ordinal}",
                    serializedParameterOrdinal,
                    method.TypeParameterSubstitutions,
                    isSerializable: false));
                continue;
            }

            var fieldId = explicitFieldId ?? serializedParameterOrdinal;
            if (assignedFieldIds.TryGetValue(fieldId, out var conflictingParameter))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(
                    InvalidRpcParameterIdDiagnostic.CreateDuplicateFieldIdDiagnostic(
                        parameter,
                        fieldId,
                        conflictingParameter));
            }

            assignedFieldIds.Add(fieldId, parameter);
            fields.Add(new MethodParameterFieldDescription(
                LibraryTypes,
                parameter,
                $"arg{parameter.Ordinal}",
                fieldId,
                method.TypeParameterSubstitutions,
                isSerializable: true));
            serializedParameterOrdinal++;
        }

        fields.Add(new TargetFieldDescription(method.Method.ContainingType));
        fields.Add(new MethodInfoFieldDescription(LibraryTypes.MethodInfo, "MethodBackingField"));

        if (method.IsCancellable)
        {
            fields.Add(new CancellationTokenSourceFieldDescription(LibraryTypes));
        }

        return fields;
    }

    internal abstract class InvokerFieldDescription(ITypeSymbol fieldType, string fieldName)
    {
        public ITypeSymbol FieldType { get; } = fieldType;
        public string FieldName { get; } = fieldName;
        public abstract bool IsSerializable { get; }
        public abstract bool IsInstanceField { get; }
    }

    internal sealed class TargetFieldDescription(ITypeSymbol fieldType) : InvokerFieldDescription(fieldType, "_target")
    {
        public override bool IsSerializable => false;
        public override bool IsInstanceField => true;
    }

    internal sealed class CancellationTokenSourceFieldDescription(LibraryTypes libraryTypes) : InvokerFieldDescription(libraryTypes.CancellationTokenSource, "_cts")
    {
        public override bool IsSerializable => false;
        public override bool IsInstanceField => true;
    }

    internal sealed class CancellationTokenFieldDescription(LibraryTypes libraryTypes) : InvokerFieldDescription(libraryTypes.CancellationToken, "_ct")
    {
        public override bool IsSerializable => false;
        public override bool IsInstanceField => true;
    }

    internal class MethodParameterFieldDescription : InvokerFieldDescription, IMemberDescription
    {
        public MethodParameterFieldDescription(
            LibraryTypes libraryTypes,
            IParameterSymbol parameter,
            string fieldName,
            uint fieldId,
            Dictionary<ITypeParameterSymbol, string> typeParameterSubstitutions,
            bool isSerializable)
            : base(parameter.Type, fieldName)
        {
            TypeParameterSubstitutions = typeParameterSubstitutions;
            FieldId = fieldId;
            LibraryTypes = libraryTypes;
            Parameter = parameter;
            if (parameter.Type.TypeKind == TypeKind.Dynamic)
            {
                TypeSyntax = PredefinedType(Token(SyntaxKind.ObjectKeyword));
                TypeName = "dynamic";
            }
            else
            {
                TypeName = Type.ToDisplayName(TypeParameterSubstitutions);
                TypeSyntax = Type.ToTypeSyntax(TypeParameterSubstitutions);
            }

            Symbol = parameter;
            IsSerializable = isSerializable;
        }

        public ISymbol Symbol { get; }
        public LibraryTypes LibraryTypes { get; }
        public Dictionary<ITypeParameterSymbol, string> TypeParameterSubstitutions { get; }
        public int ParameterOrdinal => Parameter.Ordinal;
        public uint FieldId { get; }
        public ISymbol Member => Parameter;
        public ITypeSymbol Type => FieldType;
        public INamedTypeSymbol ContainingType => Parameter.ContainingType;
        public TypeSyntax TypeSyntax { get; }
        public IParameterSymbol Parameter { get; }
        public override bool IsSerializable { get; }
        public bool IsCopyable => true;
        public override bool IsInstanceField => true;

        public string AssemblyName => Parameter.Type.ContainingAssembly.ToDisplayName();
        public string TypeName { get; }

        public string TypeNameIdentifier
        {
            get
            {
                if (Type is ITypeParameterSymbol tp && TypeParameterSubstitutions.TryGetValue(tp, out var name))
                {
                    return name;
                }

                return Type.GetValidIdentifier();
            }
        }

        public bool IsPrimaryConstructorParameter => false;

        public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax(TypeParameterSubstitutions);
    }

    internal sealed class MethodInfoFieldDescription(ITypeSymbol fieldType, string fieldName) : InvokerFieldDescription(fieldType, fieldName)
    {
        public override bool IsSerializable => false;
        public override bool IsInstanceField => false;
    }
}
