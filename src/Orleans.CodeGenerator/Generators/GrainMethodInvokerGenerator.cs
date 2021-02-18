using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.Generators
{
    /// <summary>
    /// Generates IGrainMethodInvoker implementations for grains.
    /// </summary>
    internal class GrainMethodInvokerGenerator
    {
        private readonly CodeGeneratorOptions options;
        private readonly WellKnownTypes wellKnownTypes;

        /// <summary>
        /// The next id used for generated code disambiguation purposes.
        /// </summary>
        private int nextId;

        public GrainMethodInvokerGenerator(CodeGeneratorOptions options, WellKnownTypes wellKnownTypes)
        {
            this.options = options;
            this.wellKnownTypes = wellKnownTypes;
        }

        /// <summary>
        /// Returns the name of the generated class for the provided type.
        /// </summary>
        internal static string GetGeneratedClassName(INamedTypeSymbol type) => CodeGenerator.ToolName + type.GetSuitableClassName() + "MethodInvoker";

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        internal TypeDeclarationSyntax GenerateClass(GrainInterfaceDescription description)
        {
            var generatedTypeName = description.InvokerTypeName;
            var grainType = description.Type;
            var baseTypes = new List<BaseTypeSyntax> { SimpleBaseType(wellKnownTypes.IGrainMethodInvoker.ToTypeSyntax()) };

            var genericTypes = grainType.GetHierarchyTypeParameters()
                .Select(_ => TypeParameter(_.ToString()))
                .ToArray();
            
            // Create the special method invoker marker attribute.
            var interfaceId = description.InterfaceId;
            var interfaceIdArgument = interfaceId.ToHexLiteral();
            var grainTypeArgument = TypeOfExpression(grainType.WithoutTypeParameters().ToTypeSyntax());
            var attributes = new List<AttributeSyntax>
            {
                GeneratedCodeAttributeGenerator.GetGeneratedCodeAttributeSyntax(wellKnownTypes),
                Attribute(wellKnownTypes.MethodInvokerAttribute.ToNameSyntax())
                    .AddArgumentListArguments(
                        AttributeArgument(grainTypeArgument),
                        AttributeArgument(interfaceIdArgument)),
                Attribute(wellKnownTypes.ExcludeFromCodeCoverageAttribute.ToNameSyntax())
            };

            var genericInvokerFields = GenerateGenericInvokerFields(wellKnownTypes, description.Methods);
            var members = new List<MemberDeclarationSyntax>(genericInvokerFields.Values.Select(x => x.Declaration))
            {
                GenerateInvokeMethod(wellKnownTypes, grainType, genericInvokerFields),
                GenerateInterfaceTypeProperty(wellKnownTypes, description, genericTypes),
                GenerateGetTargetMethod(wellKnownTypes, description)
            };

            // If this is an IGrainExtension, make the generated class implement IGrainExtensionMethodInvoker.
            if (grainType.HasInterface(wellKnownTypes.IGrainExtension))
            {
                baseTypes.Add(SimpleBaseType(wellKnownTypes.IGrainExtensionMethodInvoker.ToTypeSyntax()));
            }
            
            var classDeclaration =
                ClassDeclaration(generatedTypeName)
                    .AddModifiers(Token(SyntaxKind.InternalKeyword))
                    .AddBaseListTypes(baseTypes.ToArray())
                    .AddConstraintClauses(grainType.GetTypeConstraintSyntax())
                    .AddMembers(members.ToArray())
                    .AddAttributeLists(AttributeList().AddAttributes(attributes.ToArray()));
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
        /// Generates syntax for the IGrainMethodInvoker.Invoke method.
        /// </summary>
        private MethodDeclarationSyntax GenerateInvokeMethod(
            WellKnownTypes wellKnownTypes,
            INamedTypeSymbol grainType,
            Dictionary<IMethodSymbol, GenericInvokerField> genericInvokerFields)
        {
            // Get the method with the correct type.
            var invokeMethod = wellKnownTypes.IGrainMethodInvoker.Method("Invoke", wellKnownTypes.IGrainContext, wellKnownTypes.InvokeMethodRequest);

            return GenerateInvokeMethod(wellKnownTypes, grainType, invokeMethod, genericInvokerFields);
        }

        /// <summary>
        /// Generates syntax for an invoke method.
        /// </summary>
        private MethodDeclarationSyntax GenerateInvokeMethod(
            WellKnownTypes wellKnownTypes,
            INamedTypeSymbol grainType,
            IMethodSymbol invokeMethod,
            Dictionary<IMethodSymbol, GenericInvokerField> genericInvokerFields)
        {
            var parameters = invokeMethod.Parameters;

            var grainArgument = parameters[0].Name.ToIdentifierName();
            var requestArgument = parameters[1].Name.ToIdentifierName();

            var interfaceIdProperty = wellKnownTypes.InvokeMethodRequest.Property("InterfaceTypeCode");
            var methodIdProperty = wellKnownTypes.InvokeMethodRequest.Property("MethodId");
            var argumentsProperty = wellKnownTypes.InvokeMethodRequest.Property("Arguments");

            // Store the relevant values from the request in local variables.
            var interfaceIdDeclaration =
                LocalDeclarationStatement(
                    VariableDeclaration(wellKnownTypes.Int32.ToTypeSyntax())
                        .AddVariables(
                            VariableDeclarator("interfaceTypeCode")
                                .WithInitializer(EqualsValueClause(requestArgument.Member(interfaceIdProperty.Name)))));
            var interfaceIdVariable = IdentifierName("interfaceTypeCode");

            var methodIdDeclaration =
                LocalDeclarationStatement(
                    VariableDeclaration(wellKnownTypes.Int32.ToTypeSyntax())
                        .AddVariables(
                            VariableDeclarator("methodId")
                                .WithInitializer(EqualsValueClause(requestArgument.Member(methodIdProperty.Name)))));
            var methodIdVariable = IdentifierName("methodId");

            var argumentsDeclaration =
                LocalDeclarationStatement(
                    VariableDeclaration(IdentifierName("var"))
                        .AddVariables(
                            VariableDeclarator("arguments")
                                .WithInitializer(EqualsValueClause(requestArgument.Member(argumentsProperty.Name)))));
            var argumentsVariable = IdentifierName("arguments");

            var methodDeclaration = invokeMethod.GetDeclarationSyntax()
                .AddModifiers(Token(SyntaxKind.AsyncKeyword))
                .AddBodyStatements(interfaceIdDeclaration, methodIdDeclaration, argumentsDeclaration);

            var callThrowMethodNotImplemented = InvocationExpression(IdentifierName("ThrowMethodNotImplemented"))
                .WithArgumentList(ArgumentList(SeparatedList(new[]
                {
                    Argument(interfaceIdVariable),
                    Argument(methodIdVariable)
                })));

            // This method is used directly after its declaration to create blocks for each interface id, comprising
            // primarily of a nested switch statement for each of the methods in the given interface.
            BlockSyntax ComposeInterfaceBlock(INamedTypeSymbol interfaceType, SwitchStatementSyntax methodSwitch)
            {
                ExpressionSyntax targetObject;
                if (grainType.HasInterface(wellKnownTypes.IGrainExtension))
                {
                    targetObject = InvocationExpression(grainArgument.Member("GetGrainExtension", interfaceType), ArgumentList());
                }
                else
                {
                    targetObject = grainArgument.Member("GrainInstance");
                }

                var typedGrainDeclaration = LocalDeclarationStatement(
                    VariableDeclaration(IdentifierName("var"))
                        .AddVariables(
                            VariableDeclarator("casted")
                                .WithInitializer(EqualsValueClause(ParenthesizedExpression(CastExpression(interfaceType.ToTypeSyntax(), targetObject))))));

                return Block(typedGrainDeclaration,
                    methodSwitch.AddSections(SwitchSection()
                        .AddLabels(DefaultSwitchLabel())
                        .AddStatements(
                            ExpressionStatement(callThrowMethodNotImplemented),
                            ReturnStatement(LiteralExpression(SyntaxKind.NullLiteralExpression)))));
            }

            var interfaceCases = GrainInterfaceCommon.GenerateGrainInterfaceAndMethodSwitch(
                wellKnownTypes,
                grainType,
                methodIdVariable,
                methodType => GenerateInvokeForMethod(wellKnownTypes, IdentifierName("casted"), methodType, argumentsVariable, genericInvokerFields),
                ComposeInterfaceBlock);
            
            var throwInterfaceNotImplemented = GrainInterfaceCommon.GenerateMethodNotImplementedFunction(wellKnownTypes);
            var throwMethodNotImplemented = GrainInterfaceCommon.GenerateInterfaceNotImplementedFunction(wellKnownTypes);

            // Generate the default case, which will call the above local function to throw .
            var callThrowInterfaceNotImplemented = InvocationExpression(IdentifierName("ThrowInterfaceNotImplemented"))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(interfaceIdVariable))));
            var defaultCase = SwitchSection()
                .AddLabels(DefaultSwitchLabel())
                .AddStatements(
                    ExpressionStatement(callThrowInterfaceNotImplemented),
                    ReturnStatement(LiteralExpression(SyntaxKind.NullLiteralExpression)));

            var interfaceIdSwitch =
                SwitchStatement(interfaceIdVariable).AddSections(interfaceCases.ToArray()).AddSections(defaultCase);
            return methodDeclaration.AddBodyStatements(interfaceIdSwitch, throwInterfaceNotImplemented, throwMethodNotImplemented);
        }

        /// <summary>
        /// Generates syntax to invoke a method on a grain.
        /// </summary>
        private StatementSyntax[] GenerateInvokeForMethod(
            WellKnownTypes wellKnownTypes,
            ExpressionSyntax castGrain,
            IMethodSymbol method,
            ExpressionSyntax arguments,
            Dictionary<IMethodSymbol, GenericInvokerField> genericInvokerFields)
        {
            // Construct expressions to retrieve each of the method's parameters.
            var parameters = new List<ExpressionSyntax>();
            var methodParameters = method.Parameters.ToList();
            for (var i = 0; i < methodParameters.Count; i++)
            {
                var parameter = methodParameters[i];
                var parameterType = parameter.Type.ToTypeSyntax();
                var indexArg = Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(i)));
                var arg = CastExpression(
                    parameterType,
                    ElementAccessExpression(arguments).AddArgumentListArguments(indexArg));
                parameters.Add(arg);
            }

            // If the method is a generic method definition, use the generic method invoker field to invoke the method.
            if (method.IsGenericMethod)
            {
                var invokerFieldName = genericInvokerFields[method].FieldName;
                var invokerCall = InvocationExpression(
                                        IdentifierName(invokerFieldName)
                                          .Member(wellKnownTypes.IGrainMethodInvoker.Method("Invoke").Name))
                                    .AddArgumentListArguments(Argument(castGrain), Argument(arguments));
                return new StatementSyntax[] { ReturnStatement(AwaitExpression(invokerCall)) };
            }

            // Invoke the method.
            var grainMethodCall =
                    InvocationExpression(castGrain.Member(method.Name))
                      .AddArgumentListArguments(parameters.Select(Argument).ToArray());

            // For void methods, invoke the method and return null.
            if (method.ReturnsVoid)
            {
                return new StatementSyntax[]
                {
                    ExpressionStatement(grainMethodCall),
                    ReturnStatement(LiteralExpression(SyntaxKind.NullLiteralExpression))
                };
            }

            // For methods which return an awaitable type which has no result type, await the method and return null.
            if (method.ReturnType.Method("GetAwaiter").ReturnType.Method("GetResult").ReturnsVoid)
            {
                return new StatementSyntax[]
                {
                    ExpressionStatement(AwaitExpression(grainMethodCall)),
                    ReturnStatement(LiteralExpression(SyntaxKind.NullLiteralExpression))
                };
            }

            return new StatementSyntax[]
            {
                ReturnStatement(AwaitExpression(grainMethodCall))
            };
        }

        /// <summary>
        /// Generates GenericMethodInvoker fields for the generic methods in <paramref name="methodDescriptions"/>.
        /// </summary>
        private Dictionary<IMethodSymbol, GenericInvokerField> GenerateGenericInvokerFields(WellKnownTypes wellKnownTypes, List<GrainMethodDescription> methodDescriptions)
        {
            if (!(wellKnownTypes.GenericMethodInvoker is WellKnownTypes.Some genericMethodInvoker)) return new Dictionary<IMethodSymbol, GenericInvokerField>(SymbolEqualityComparer.Default);

            var result = new Dictionary<IMethodSymbol, GenericInvokerField>(methodDescriptions.Count, SymbolEqualityComparer.Default);
            foreach (var description in methodDescriptions)
            {
                var method = description.Method;
                if (!method.IsGenericMethod) continue;
                result[method] = GenerateGenericInvokerField(method, genericMethodInvoker.Value);
            }

            return result;
        }

        /// <summary>
        /// Generates a GenericMethodInvoker field for the provided generic method.
        /// </summary>
        private GenericInvokerField GenerateGenericInvokerField(IMethodSymbol method, INamedTypeSymbol genericMethodInvoker)
        {
            var fieldName = $"GenericInvoker_{method.Name}_{Interlocked.Increment(ref nextId):X}";
            var fieldInfoVariable =
                VariableDeclarator(fieldName)
                  .WithInitializer(
                      EqualsValueClause(
                          ObjectCreationExpression(genericMethodInvoker.ToTypeSyntax())
                            .AddArgumentListArguments(
                                Argument(TypeOfExpression(method.ContainingType.ToTypeSyntax())),
                                Argument(method.Name.ToLiteralExpression()),
                                Argument(
                                    LiteralExpression(
                                        SyntaxKind.NumericLiteralExpression,
                                        Literal(method.TypeArguments.Length))))));

            var declaration =
                FieldDeclaration(
                      VariableDeclaration(genericMethodInvoker.ToTypeSyntax()).AddVariables(fieldInfoVariable))
                  .AddModifiers(
                      Token(SyntaxKind.PrivateKeyword),
                      Token(SyntaxKind.StaticKeyword),
                      Token(SyntaxKind.ReadOnlyKeyword));

            return new GenericInvokerField(fieldName, declaration);
        }

        private PropertyDeclarationSyntax GenerateInterfaceTypeProperty(WellKnownTypes wellKnownTypes, GrainInterfaceDescription description, TypeParameterSyntax[] genericTypes)
        {
            var property = wellKnownTypes.IGrainMethodInvoker.Property("InterfaceType");
            var returnValue =  TypeOfExpression(description.Type.ToTypeSyntax());
            return
                PropertyDeclaration(wellKnownTypes.Type.ToTypeSyntax(), property.Name)
                    .WithExpressionBody(ArrowExpressionClause(returnValue))
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        }

        private MethodDeclarationSyntax GenerateGetTargetMethod(WellKnownTypes wellKnownTypes, GrainInterfaceDescription description)
        {
            var method = wellKnownTypes.IGrainMethodInvoker.Method("GetTarget");
            var grainContextParameter = method.Parameters[0].Name.ToIdentifierName();

            ExpressionSyntax returnValue;
            if (description.Type.HasInterface(wellKnownTypes.IGrainExtension))
            {
                returnValue = InvocationExpression(grainContextParameter.Member("GetGrainExtension", description.Type), ArgumentList());
            }
            else
            {
                returnValue = grainContextParameter.Member("GrainInstance");
            }

            return
                method.GetDeclarationSyntax()
                    .WithExpressionBody(ArrowExpressionClause(returnValue))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        }

        private readonly struct GenericInvokerField
        {
            public GenericInvokerField(string fieldName, MemberDeclarationSyntax declaration)
            {
                this.FieldName = fieldName;
                this.Declaration = declaration;
            }

            public string FieldName { get; }
            public MemberDeclarationSyntax Declaration { get; }
        }
    }
}
