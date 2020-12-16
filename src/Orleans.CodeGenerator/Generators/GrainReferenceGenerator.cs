using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Generators
{
    /// <summary>
    /// Generates GrainReference implementations for grains.
    /// </summary>
    internal class GrainReferenceGenerator
    {
        private readonly CodeGeneratorOptions options;
        private readonly WellKnownTypes wellKnownTypes;

        public GrainReferenceGenerator(CodeGeneratorOptions options, WellKnownTypes wellKnownTypes)
        {
            this.options = options;
            this.wellKnownTypes = wellKnownTypes;
        }

        /// <summary>
        /// Returns the name of the generated class for the provided type.
        /// </summary>
        internal static string GetGeneratedClassName(INamedTypeSymbol type)
        {
            return CodeGenerator.ToolName + type.GetSuitableClassName() + "Reference";
        }

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        internal TypeDeclarationSyntax GenerateClass(GrainInterfaceDescription description)
        {
            var generatedTypeName = description.ReferenceTypeName;
            var grainType = description.Type;
            var genericTypes = grainType.GetHierarchyTypeParameters()
                .Select(_ => TypeParameter(_.ToString()))
                .ToArray();

            // Create the special marker attribute.
            var grainTypeArgument = TypeOfExpression(grainType.WithoutTypeParameters().ToTypeSyntax());

            var attributes = AttributeList()
                .AddAttributes(
                    GeneratedCodeAttributeGenerator.GetGeneratedCodeAttributeSyntax(wellKnownTypes),
                    Attribute(wellKnownTypes.SerializableAttribute.ToNameSyntax()),
                    Attribute(wellKnownTypes.ExcludeFromCodeCoverageAttribute.ToNameSyntax()),
                    Attribute(wellKnownTypes.GrainReferenceAttribute.ToNameSyntax())
                        .AddArgumentListArguments(AttributeArgument(grainTypeArgument)));

            var classDeclaration =
                ClassDeclaration(generatedTypeName)
                    .AddModifiers(Token(SyntaxKind.InternalKeyword))
                    .AddBaseListTypes(
                        SimpleBaseType(wellKnownTypes.GrainReference.ToTypeSyntax()),
                        SimpleBaseType(grainType.ToTypeSyntax()))
                    .AddConstraintClauses(grainType.GetTypeConstraintSyntax())
                    .AddMembers(GenerateConstructors(generatedTypeName))
                    .AddMembers(
                        GrainInterfaceCommon.GenerateInterfaceIdProperty(this.wellKnownTypes, description).AddModifiers(Token(SyntaxKind.OverrideKeyword)),
                        GrainInterfaceCommon.GenerateInterfaceVersionProperty(this.wellKnownTypes, description).AddModifiers(Token(SyntaxKind.OverrideKeyword)),
                        GenerateInterfaceNameProperty(description),
                        GenerateGetMethodNameMethod(description))
                    .AddMembers(GenerateInvokeMethods(description))
                    .AddAttributeLists(attributes);
            if (genericTypes.Length > 0)
            {
                classDeclaration = classDeclaration.AddTypeParameterListParameters(genericTypes);
            }

            if (this.options.DebuggerStepThrough)
            {
                var debuggerStepThroughAttribute = Attribute(this.wellKnownTypes.DebuggerStepThroughAttribute.ToNameSyntax());
                classDeclaration = classDeclaration.AddAttributeLists(AttributeList().AddAttributes(debuggerStepThroughAttribute));
            }

            return classDeclaration;
        }

        /// <summary>
        /// Generates constructors.
        /// </summary>
        private MemberDeclarationSyntax[] GenerateConstructors(string className)
        {
            var baseConstructors =
                wellKnownTypes.GrainReference.Constructors.Where(c => c.DeclaredAccessibility != Accessibility.Private);
            var constructors = new List<MemberDeclarationSyntax>();
            foreach (var baseConstructor in baseConstructors)
            {
                var args = baseConstructor.Parameters
                    .Select(arg => Argument(arg.Name.ToIdentifierName()))
                    .ToArray();
                var declaration =
                    baseConstructor.GetConstructorDeclarationSyntax(className)
                        .WithInitializer(
                            ConstructorInitializer(SyntaxKind.BaseConstructorInitializer)
                                .AddArgumentListArguments(args))
                        .AddBodyStatements();
                constructors.Add(declaration);
            }

            return constructors.ToArray();
        }

        /// <summary>
        /// Generates invoker methods.
        /// </summary>
        private MemberDeclarationSyntax[] GenerateInvokeMethods(GrainInterfaceDescription description)
        {
            var baseReference = BaseExpression();
            var methods = description.Methods;
            var members = new List<MemberDeclarationSyntax>();
            foreach (var methodDescription in methods)
            {
                var method = methodDescription.Method;
                var methodIdArgument = Argument(methodDescription.MethodId.ToHexLiteral());

                // Construct a new object array from all method arguments.
                var parameters = method.Parameters.Select((p, i) => (p, GetSanitizedName(p, i))).ToList<(IParameterSymbol Symbol, string Name)>();
                var body = new List<StatementSyntax>();
                foreach (var parameter in parameters)
                {
                    if (parameter.Symbol.Type.HasInterface(wellKnownTypes.IGrainObserver))
                    {
                        body.Add(
                            ExpressionStatement(
                                InvocationExpression(wellKnownTypes.GrainFactoryBase.ToNameSyntax().Member("CheckGrainObserverParamInternal"))
                                    .AddArgumentListArguments(Argument(parameter.Name.ToIdentifierName()))));
                    }
                }

                // Get the parameters argument value.
                var objectArrayType = wellKnownTypes.Object.ToTypeSyntax().GetArrayTypeSyntax();
                ExpressionSyntax args;
                if (method.IsGenericMethod)
                {
                    // Create an arguments array which includes the method's type parameters followed by the method's parameter list.
                    var allParameters = new List<ExpressionSyntax>();
                    foreach (var typeParameter in method.TypeParameters)
                    {
                        allParameters.Add(TypeOfExpression(typeParameter.ToTypeSyntax()));
                    }

                    // PR: 6844 (fix generic overload selector in GenericMethodInvoker)
                    allParameters.AddRange(parameters.Select(p => TypeOfExpression(p.Symbol.Type.ToTypeSyntax())));
                    
                    allParameters.AddRange(parameters.Select(p => GetParameterForInvocation(p.Symbol, p.Name)));

                    args =
                        ArrayCreationExpression(objectArrayType)
                        .WithInitializer(
                            InitializerExpression(SyntaxKind.ArrayInitializerExpression)
                              .AddExpressions(allParameters.ToArray()));
                }
                else if (parameters.Count == 0)
                {
                    args = LiteralExpression(SyntaxKind.NullLiteralExpression);
                }
                else
                {
                    args =
                        ArrayCreationExpression(objectArrayType)
                            .WithInitializer(
                                InitializerExpression(SyntaxKind.ArrayInitializerExpression)
                                    .AddExpressions(parameters.Select((p => GetParameterForInvocation(p.Symbol, p.Name))).ToArray()));
                }

                var options = GetInvokeOptions(method);

                // Construct the invocation call.
                bool asyncMethod;
                var isOneWayTask = method.HasAttribute(wellKnownTypes.OneWayAttribute);
                if (method.ReturnsVoid || isOneWayTask)
                {
                    // One-way methods are never marked async.
                    asyncMethod = false;

                    var invocation = InvocationExpression(baseReference.Member("InvokeOneWayMethod"))
                        .AddArgumentListArguments(methodIdArgument)
                        .AddArgumentListArguments(Argument(args));

                    if (options != null)
                    {
                        invocation = invocation.AddArgumentListArguments(options);
                    }

                    body.Add(ExpressionStatement(invocation));

                    if (isOneWayTask)
                    {
                        if (SymbolEqualityComparer.Default.Equals(wellKnownTypes.Task, method.ReturnType))
                        {
                            var done = wellKnownTypes.Task.ToNameSyntax().Member((object _) => Task.CompletedTask);
                            body.Add(ReturnStatement(done));
                        }
                        else if (wellKnownTypes.ValueTask is WellKnownTypes.Some valueTask
                            && SymbolEqualityComparer.Default.Equals(valueTask.Value, method.ReturnType))
                        {
                            body.Add(ReturnStatement(LiteralExpression(SyntaxKind.DefaultLiteralExpression)));
                        }
                        else
                        {
                            throw new CodeGenerationException(
                                $"Method {method} is marked with [{wellKnownTypes.OneWayAttribute.Name}], " +
                                $"but has a return type which is not assignable from {typeof(Task)} or {typeof(ValueTask)}");
                        }
                    }
                }
                else if (method.ReturnType is INamedTypeSymbol methodReturnType)
                {
                    // If the method doesn't return a Task type (eg, it returns ValueTask<T>), then we must make an async method and await the invocation result.
                    var isTaskMethod = SymbolEqualityComparer.Default.Equals(wellKnownTypes.Task, methodReturnType)
                                       || methodReturnType.IsGenericType && SymbolEqualityComparer.Default.Equals(wellKnownTypes.Task_1, methodReturnType.ConstructedFrom);
                    asyncMethod = !isTaskMethod;

                    var returnType = methodReturnType.IsGenericType
                        ? methodReturnType.TypeArguments[0]
                        : wellKnownTypes.Object;
                    var invokeMethodAsync = "InvokeMethodAsync".ToGenericName().AddTypeArgumentListArguments(returnType.ToTypeSyntax());
                    var invocation =
                        InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                baseReference,
                                invokeMethodAsync))
                            .AddArgumentListArguments(methodIdArgument)
                            .AddArgumentListArguments(Argument(args));

                    if (options != null)
                    {
                        invocation = invocation.AddArgumentListArguments(options);
                    }

                    var methodResult = asyncMethod ? AwaitExpression(invocation) : (ExpressionSyntax)invocation;

                    if (this.wellKnownTypes.ValueTask is WellKnownTypes.Some valueTask
                        && SymbolEqualityComparer.Default.Equals(valueTask.Value, methodReturnType))
                    {
                        body.Add(ExpressionStatement(methodResult));
                    }
                    else
                    {
                        body.Add(ReturnStatement(methodResult));
                    }
                }
                else throw new NotSupportedException($"Method {method} has unsupported return type, {method.ReturnType}.");

                var paramDeclaration = method.Parameters.Select((p, i) => Parameter(GetSanitizedName(p, i).ToIdentifier()).WithType(p.Type.ToTypeSyntax()));
                var methodDeclaration = method.GetDeclarationSyntax()
                    .WithParameterList(ParameterList().AddParameters(paramDeclaration.ToArray()))
                    .WithModifiers(TokenList())
                    .WithExplicitInterfaceSpecifier(ExplicitInterfaceSpecifier(method.ContainingType.ToNameSyntax()))
                    .AddBodyStatements(body.ToArray())
                    // Since explicit implementation is used, constraints must not be specified.
                    .WithConstraintClauses(new SyntaxList<TypeParameterConstraintClauseSyntax>());

                if (asyncMethod) methodDeclaration = methodDeclaration.AddModifiers(Token(SyntaxKind.AsyncKeyword));

                members.Add(methodDeclaration);
            }

            return members.ToArray();

            ExpressionSyntax GetParameterForInvocation(IParameterSymbol arg, string name)
            {
                var identifier = name.ToIdentifierName();

                // Addressable arguments must be converted to references before passing.
                if (arg.Type.HasInterface(wellKnownTypes.IAddressable)
                    && arg.Type.TypeKind == TypeKind.Interface)
                {
                    return
                        ConditionalExpression(
                            BinaryExpression(SyntaxKind.IsExpression, identifier, wellKnownTypes.Grain.ToTypeSyntax()),
                            InvocationExpression(identifier.Member("AsReference".ToGenericName().AddTypeArgumentListArguments(arg.Type.ToTypeSyntax()))),
                            identifier);
                }

                return identifier;
            }

            static string GetSanitizedName(IParameterSymbol parameter, int index)
            {
                var parameterName = string.IsNullOrWhiteSpace(parameter.Name) ? "arg" : parameter.Name;
                return string.Format(CultureInfo.InvariantCulture, "{0}{1:G}", parameterName, index);
            }
        }

        /// <summary>
        /// Returns syntax for the options argument to GrainReference.InvokeMethodAsync{T} and GrainReference.InvokeOneWayMethod.
        /// </summary>
        private ArgumentSyntax GetInvokeOptions(IMethodSymbol method)
        {
            var options = new List<ExpressionSyntax>();
            var imo = wellKnownTypes.InvokeMethodOptions.ToNameSyntax();
            if (method.HasAttribute(wellKnownTypes.ReadOnlyAttribute))
            {
                options.Add(imo.Member("ReadOnly"));
            }

            if (method.HasAttribute(wellKnownTypes.UnorderedAttribute))
            {
                options.Add(imo.Member("Unordered"));
            }

            if (method.HasAttribute(wellKnownTypes.AlwaysInterleaveAttribute))
            {
                options.Add(imo.Member("AlwaysInterleave"));
            }

            if (method.GetAttribute(wellKnownTypes.TransactionAttribute, out var attr))
            {
                var enumType = wellKnownTypes.TransactionOption;
                var txRequirement = (int)attr.ConstructorArguments.First().Value;
                var values = enumType.GetMembers().OfType<IFieldSymbol>().ToList();
                var mapping = values.ToDictionary(m => (int)m.ConstantValue, m => m.Name);
                if (!mapping.TryGetValue(txRequirement, out var value))
                {
                    throw new NotSupportedException(
                        $"Transaction requirement {txRequirement} on method {method} was not understood."
                        + $" Known values: {string.Join(", ", mapping.Select(kv => $"{kv.Key} ({kv.Value})"))}");
                }

                switch (value)
                {
                    case "Suppress":
                        options.Add(imo.Member("TransactionSuppress"));
                        break;
                    case "CreateOrJoin":
                        options.Add(imo.Member("TransactionCreateOrJoin"));
                        break;
                    case "Create":
                        options.Add(imo.Member("TransactionCreate"));
                        break;
                    case "Join":
                        options.Add(imo.Member("TransactionJoin"));
                        break;
                    case "Supported":
                        options.Add(imo.Member("TransactionSupported"));
                        break;
                    case "NotAllowed":
                        options.Add(imo.Member("TransactionNotAllowed"));
                        break;
                    default:
                        throw new NotSupportedException($"Transaction requirement {value} on method {method} was not understood.");
                }
            }

            ExpressionSyntax allOptions;
            if (options.Count <= 1)
            {
                allOptions = options.FirstOrDefault();
            }
            else
            {
                allOptions =
                    options.Aggregate((a, b) => BinaryExpression(SyntaxKind.BitwiseOrExpression, a, b));
            }

            if (allOptions == null)
            {
                return null;
            }

            return Argument(NameColon("options"), Token(SyntaxKind.None), allOptions);
        }

        private MemberDeclarationSyntax GenerateInterfaceNameProperty(GrainInterfaceDescription description)
        {
            var returnValue = description.Type.Name.ToLiteralExpression();
            return
                PropertyDeclaration(wellKnownTypes.String.ToTypeSyntax(), "InterfaceName")
                    .WithExpressionBody(ArrowExpressionClause(returnValue))
                    .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.OverrideKeyword))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        }

        private MethodDeclarationSyntax GenerateGetMethodNameMethod(GrainInterfaceDescription description)
        {
            var method = wellKnownTypes.GrainReference.Method("GetMethodName");
            var methodDeclaration = method.GetDeclarationSyntax().AddModifiers(Token(SyntaxKind.OverrideKeyword));
            var parameters = method.Parameters;

            var interfaceIdArgument = parameters[0].Name.ToIdentifierName();
            var methodIdArgument = parameters[1].Name.ToIdentifierName();

            var callThrowMethodNotImplemented = InvocationExpression(IdentifierName("ThrowMethodNotImplemented"))
                .WithArgumentList(ArgumentList(SeparatedList(new[]
                {
                    Argument(interfaceIdArgument),
                    Argument(methodIdArgument)
                })));

            // This method is used directly after its declaration to create blocks for each interface id, comprising
            // primarily of a nested switch statement for each of the methods in the given interface.
            BlockSyntax ComposeInterfaceBlock(INamedTypeSymbol interfaceType, SwitchStatementSyntax methodSwitch)
            {
                return Block(methodSwitch.AddSections(SwitchSection()
                        .AddLabels(DefaultSwitchLabel())
                        .AddStatements(
                            ExpressionStatement(callThrowMethodNotImplemented),
                            ReturnStatement(LiteralExpression(SyntaxKind.NullLiteralExpression)))));
            }

            var interfaceCases = GrainInterfaceCommon.GenerateGrainInterfaceAndMethodSwitch(
                wellKnownTypes,
                description.Type,
                methodIdArgument,
                methodType => new StatementSyntax[] { ReturnStatement(methodType.Name.ToLiteralExpression()) },
                ComposeInterfaceBlock);

            // Generate the default case, which will throw a NotImplementedException.
            var callThrowInterfaceNotImplemented = InvocationExpression(IdentifierName("ThrowInterfaceNotImplemented"))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(interfaceIdArgument))));
            var defaultCase = SwitchSection()
                .AddLabels(DefaultSwitchLabel())
                .AddStatements(
                    ExpressionStatement(callThrowInterfaceNotImplemented),
                    ReturnStatement(LiteralExpression(SyntaxKind.NullLiteralExpression)));

            var throwInterfaceNotImplemented = GrainInterfaceCommon.GenerateMethodNotImplementedFunction(wellKnownTypes);
            var throwMethodNotImplemented = GrainInterfaceCommon.GenerateInterfaceNotImplementedFunction(wellKnownTypes);

            var interfaceIdSwitch =
                SwitchStatement(interfaceIdArgument).AddSections(interfaceCases.ToArray()).AddSections(defaultCase);
            return methodDeclaration.AddBodyStatements(interfaceIdSwitch, throwInterfaceNotImplemented, throwMethodNotImplemented);
        }
    }
}
