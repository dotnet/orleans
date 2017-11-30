namespace Orleans.CodeGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Runtime;
    using GrainInterfaceUtils = Orleans.CodeGeneration.GrainInterfaceUtils;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Code generator which generates <see cref="IGrainMethodInvoker"/> for grains.
    /// </summary>
    internal static class GrainMethodInvokerGenerator
    {
        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "MethodInvoker";

        /// <summary>
        /// Returns the name of the generated class for the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The name of the generated class for the provided type.</returns>
        internal static string GetGeneratedClassName(Type type) => CodeGeneratorCommon.ClassPrefix + TypeUtils.GetSuitableClassName(type) + ClassSuffix;

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        /// <param name="grainType">The grain interface type.</param>
        /// <param name="className">The name for the generated class.</param>
        /// <returns>
        /// The generated class.
        /// </returns>
        internal static TypeDeclarationSyntax GenerateClass(Type grainType, string className)
        {
            var baseTypes = new List<BaseTypeSyntax> { SF.SimpleBaseType(typeof(IGrainMethodInvoker).GetTypeSyntax()) };

            var grainTypeInfo = grainType.GetTypeInfo();
            var genericTypes = grainTypeInfo.IsGenericTypeDefinition
                                   ? grainType.GetGenericArguments()
                                         .Select(_ => SF.TypeParameter(_.ToString()))
                                         .ToArray()
                                   : new TypeParameterSyntax[0];

            // Create the special method invoker marker attribute.
            var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(grainType);
            var interfaceIdArgument = SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(interfaceId));
            var grainTypeArgument = SF.TypeOfExpression(grainType.GetTypeSyntax(includeGenericParameters: false));
            var attributes = new List<AttributeSyntax>
            {
                CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                SF.Attribute(typeof(MethodInvokerAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(grainTypeArgument),
                        SF.AttributeArgument(interfaceIdArgument)),
                SF.Attribute(typeof(ExcludeFromCodeCoverageAttribute).GetNameSyntax())
            };

            var members = new List<MemberDeclarationSyntax>(GenerateGenericInvokerFields(grainType))
            {
                GenerateInvokeMethod(grainType),
                GenerateInterfaceIdProperty(grainType),
                GenerateInterfaceVersionProperty(grainType),
            };

            // If this is an IGrainExtension, make the generated class implement IGrainExtensionMethodInvoker.
            if (typeof(IGrainExtension).GetTypeInfo().IsAssignableFrom(grainTypeInfo))
            {
                baseTypes.Add(SF.SimpleBaseType(typeof(IGrainExtensionMethodInvoker).GetTypeSyntax()));
                members.Add(GenerateExtensionInvokeMethod(grainType));
            }
            
            var classDeclaration =
                SF.ClassDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                    .AddBaseListTypes(baseTypes.ToArray())
                    .AddConstraintClauses(grainType.GetTypeConstraintSyntax())
                    .AddMembers(members.ToArray())
                    .AddAttributeLists(SF.AttributeList().AddAttributes(attributes.ToArray()));
            if (genericTypes.Length > 0)
            {
                classDeclaration = classDeclaration.AddTypeParameterListParameters(genericTypes);
            }

            return classDeclaration;
        }

        /// <summary>
        /// Returns method declaration syntax for the InterfaceId property.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <returns>Method declaration syntax for the InterfaceId property.</returns>
        private static MemberDeclarationSyntax GenerateInterfaceIdProperty(Type grainType)
        {
            var property = TypeUtils.Member((IGrainMethodInvoker _) => _.InterfaceId);
            var returnValue = SF.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SF.Literal(GrainInterfaceUtils.GetGrainInterfaceId(grainType)));
            return
                SF.PropertyDeclaration(typeof(int).GetTypeSyntax(), property.Name)
                    .AddAccessorListAccessors(
                        SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .AddBodyStatements(SF.ReturnStatement(returnValue)))
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword));
        }

        private static MemberDeclarationSyntax GenerateInterfaceVersionProperty(Type grainType)
        {
            var property = TypeUtils.Member((IGrainMethodInvoker _) => _.InterfaceVersion);
            var returnValue = SF.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SF.Literal(GrainInterfaceUtils.GetGrainInterfaceVersion(grainType))); 
            return
                SF.PropertyDeclaration(typeof(ushort).GetTypeSyntax(), property.Name)
                    .AddAccessorListAccessors(
                        SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .AddBodyStatements(SF.ReturnStatement(returnValue)))
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword));
        }

        /// <summary>
        /// Generates syntax for the <see cref="IGrainMethodInvoker.Invoke"/> method.
        /// </summary>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
        /// <returns>
        /// Syntax for the <see cref="IGrainMethodInvoker.Invoke"/> method.
        /// </returns>
        private static MethodDeclarationSyntax GenerateInvokeMethod(Type grainType)
        {
            // Get the method with the correct type.
            var invokeMethod =
                TypeUtils.Method(
                    (IGrainMethodInvoker x) =>
                    x.Invoke(default(IAddressable), default(InvokeMethodRequest)));

            return GenerateInvokeMethod(grainType, invokeMethod);
        }

        /// <summary>
        /// Generates syntax for the <see cref="IGrainExtensionMethodInvoker"/> invoke method.
        /// </summary>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
        /// <returns>
        /// Syntax for the <see cref="IGrainExtensionMethodInvoker"/> invoke method.
        /// </returns>
        private static MethodDeclarationSyntax GenerateExtensionInvokeMethod(Type grainType)
        {
            // Get the method with the correct type.
            var invokeMethod =
                TypeUtils.Method(
                    (IGrainExtensionMethodInvoker x) =>
                    x.Invoke(default(IGrainExtension), default(InvokeMethodRequest)));

            return GenerateInvokeMethod(grainType, invokeMethod);
        }

        /// <summary>
        /// Generates syntax for an invoke method.
        /// </summary>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
        /// <param name="invokeMethod">
        /// The invoke method to generate.
        /// </param>
        /// <returns>
        /// Syntax for an invoke method.
        /// </returns>
        private static MethodDeclarationSyntax GenerateInvokeMethod(Type grainType, MethodInfo invokeMethod)
        {
            var parameters = invokeMethod.GetParameters();

            var grainArgument = parameters[0].Name.ToIdentifierName();
            var requestArgument = parameters[1].Name.ToIdentifierName();

            // Store the relevant values from the request in local variables.
            var interfaceIdDeclaration =
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(typeof(int).GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("interfaceId")
                                .WithInitializer(SF.EqualsValueClause(requestArgument.Member((InvokeMethodRequest _) => _.InterfaceId)))));
            var interfaceIdVariable = SF.IdentifierName("interfaceId");

            var methodIdDeclaration =
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(typeof(int).GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("methodId")
                                .WithInitializer(SF.EqualsValueClause(requestArgument.Member((InvokeMethodRequest _) => _.MethodId)))));
            var methodIdVariable = SF.IdentifierName("methodId");

            var argumentsDeclaration =
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(typeof(InvokeMethodArguments).GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("arguments")
                                .WithInitializer(SF.EqualsValueClause(requestArgument.Member((InvokeMethodRequest _) => _.Arguments)))));
            var argumentsVariable = SF.IdentifierName("arguments");

            var methodDeclaration = invokeMethod.GetDeclarationSyntax()
                .AddModifiers(SF.Token(SyntaxKind.AsyncKeyword))
                .AddBodyStatements(interfaceIdDeclaration, methodIdDeclaration, argumentsDeclaration);

            var interfaceCases = CodeGeneratorCommon.GenerateGrainInterfaceAndMethodSwitch(
                grainType,
                methodIdVariable,
                methodType => GenerateInvokeForMethod(grainType, grainArgument, methodType, argumentsVariable));

            // Generate the default case, which will throw a NotImplementedException.
            var errorMessage = SF.BinaryExpression(
                SyntaxKind.AddExpression,
                "interfaceId=".GetLiteralExpression(),
                interfaceIdVariable);
            var throwStatement =
                SF.ThrowStatement(
                    SF.ObjectCreationExpression(typeof(NotImplementedException).GetTypeSyntax())
                        .AddArgumentListArguments(SF.Argument(errorMessage)));
            var defaultCase = SF.SwitchSection().AddLabels(SF.DefaultSwitchLabel()).AddStatements(throwStatement);
            var interfaceIdSwitch =
                SF.SwitchStatement(interfaceIdVariable).AddSections(interfaceCases.ToArray()).AddSections(defaultCase);

            // If the provided grain is null, throw an argument exception.
            var argumentNullException =
                SF.ObjectCreationExpression(typeof(ArgumentNullException).GetTypeSyntax())
                    .AddArgumentListArguments(SF.Argument(parameters[0].Name.GetLiteralExpression()));
            var grainArgumentCheck =
                SF.IfStatement(
                    SF.BinaryExpression(
                        SyntaxKind.EqualsExpression,
                        grainArgument,
                        SF.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                    SF.ThrowStatement(argumentNullException));

            return methodDeclaration.AddBodyStatements(grainArgumentCheck, interfaceIdSwitch);
        }

        /// <summary>
        /// Generates syntax to invoke a method on a grain.
        /// </summary>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
        /// <param name="grain">
        /// The grain instance expression.
        /// </param>
        /// <param name="method">
        /// The method.
        /// </param>
        /// <param name="arguments">
        /// The arguments expression.
        /// </param>
        /// <returns>
        /// Syntax to invoke a method on a grain.
        /// </returns>
        private static StatementSyntax[] GenerateInvokeForMethod(
            Type grainType,
            IdentifierNameSyntax grain,
            MethodInfo method,
            ExpressionSyntax arguments)
        {
            var castGrain = SF.ParenthesizedExpression(SF.CastExpression(grainType.GetTypeSyntax(), grain));

            // Construct expressions to retrieve each of the method's parameters.
            var parameters = new List<ExpressionSyntax>();
            var methodParameters = method.GetParameters().ToList();
            for (var i = 0; i < methodParameters.Count; i++)
            {
                var parameter = methodParameters[i];
                var parameterType = parameter.ParameterType.GetTypeSyntax();
                var indexArg = SF.Argument(SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(i)));
                var arg = SF.CastExpression(
                    parameterType,
                    SF.ElementAccessExpression(arguments).AddArgumentListArguments(indexArg));
                parameters.Add(arg);
            }

            // If the method is a generic method definition, use the generic method invoker field to invoke the method.
            if (method.IsGenericMethodDefinition)
            {
                var invokerFieldName = GetGenericMethodInvokerFieldName(method);
                var invokerCall = SF.InvocationExpression(
                                        SF.IdentifierName(invokerFieldName)
                                          .Member((GenericMethodInvoker invoker) => invoker.Invoke(null, default(InvokeMethodArguments))))
                                    .AddArgumentListArguments(SF.Argument(grain), SF.Argument(arguments));
                return new StatementSyntax[] { SF.ReturnStatement(SF.AwaitExpression(invokerCall)) };
            }

            // Invoke the method.
            var grainMethodCall =
                    SF.InvocationExpression(castGrain.Member(method.Name))
                      .AddArgumentListArguments(parameters.Select(SF.Argument).ToArray());

            // For void methods, invoke the method and return null.
            if (method.ReturnType == typeof(void))
            {
                return new StatementSyntax[]
                {
                    SF.ExpressionStatement(grainMethodCall),
                    SF.ReturnStatement(SF.LiteralExpression(SyntaxKind.NullLiteralExpression))
                };
            }

            // For methods which return non-generic Task, await the method and return null.
            if (method.ReturnType == typeof(Task))
            {
                return new StatementSyntax[]
                {
                    SF.ExpressionStatement(SF.AwaitExpression(grainMethodCall)),
                    SF.ReturnStatement(SF.LiteralExpression(SyntaxKind.NullLiteralExpression))
                };
            }
            
            return new StatementSyntax[]
            {
                SF.ReturnStatement(SF.AwaitExpression(grainMethodCall))
            };
        }

        /// <summary>
        /// Generates <see cref="GenericMethodInvoker"/> fields for the generic methods in <paramref name="grainType"/>.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <returns>The generated fields.</returns>
        private static MemberDeclarationSyntax[] GenerateGenericInvokerFields(Type grainType)
        {
            var methods = GrainInterfaceUtils.GetMethods(grainType);

            var result = new List<MemberDeclarationSyntax>();
            foreach (var method in methods)
            {
                if (!method.IsGenericMethodDefinition) continue;
                result.Add(GenerateGenericInvokerField(method));
            }

            return result.ToArray();
        }

        /// <summary>
        /// Generates a <see cref="GenericMethodInvoker"/> field for the provided generic method.
        /// </summary>
        /// <param name="method">The method.</param>
        /// <returns>The generated field.</returns>
        private static MemberDeclarationSyntax GenerateGenericInvokerField(MethodInfo method)
        {
            var fieldInfoVariable =
                SF.VariableDeclarator(GetGenericMethodInvokerFieldName(method))
                  .WithInitializer(
                      SF.EqualsValueClause(
                          SF.ObjectCreationExpression(typeof(GenericMethodInvoker).GetTypeSyntax())
                            .AddArgumentListArguments(
                                SF.Argument(SF.TypeOfExpression(method.DeclaringType.GetTypeSyntax())),
                                SF.Argument(method.Name.GetLiteralExpression()),
                                SF.Argument(
                                    SF.LiteralExpression(
                                        SyntaxKind.NumericLiteralExpression,
                                        SF.Literal(method.GetGenericArguments().Length))))));

            return
                SF.FieldDeclaration(
                      SF.VariableDeclaration(typeof(GenericMethodInvoker).GetTypeSyntax()).AddVariables(fieldInfoVariable))
                  .AddModifiers(
                      SF.Token(SyntaxKind.PrivateKeyword),
                      SF.Token(SyntaxKind.StaticKeyword),
                      SF.Token(SyntaxKind.ReadOnlyKeyword));
        }

        /// <summary>
        /// Returns the name of the <see cref="GenericMethodInvoker"/> field corresponding to <paramref name="method"/>.
        /// </summary>
        /// <param name="method">The method.</param>
        /// <returns>The name of the invoker field corresponding to the provided method.</returns>
        private static string GetGenericMethodInvokerFieldName(MethodInfo method)
        {
            return method.Name + string.Join("_", method.GetGenericArguments().Select(arg => arg.Name));
        }
    }
}
