namespace Orleans.CodeGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
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
    /// Code generator which generates <see cref="GrainReference"/>s for grains.
    /// </summary>
    public static class GrainReferenceGenerator
    {
        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "Reference";

        /// <summary>
        /// A reference to the CheckGrainObserverParamInternal method.
        /// </summary>
        private static readonly Expression<Action> CheckGrainObserverParamInternalExpression =
            () => GrainFactoryBase.CheckGrainObserverParamInternal(null);
        
        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        /// <param name="grainType">
        /// The grain interface type.
        /// </param>
        /// <param name="onEncounteredType">
        /// The callback which is invoked when a type is encountered.
        /// </param>
        /// <returns>
        /// The generated class.
        /// </returns>
        internal static TypeDeclarationSyntax GenerateClass(Type grainType, Action<Type> onEncounteredType)
        {
            var grainTypeInfo = grainType.GetTypeInfo();
            var genericTypes = grainTypeInfo.IsGenericTypeDefinition
                                   ? grainTypeInfo.GetGenericArguments()
                                         .Select(_ => SF.TypeParameter(_.ToString()))
                                         .ToArray()
                                   : new TypeParameterSyntax[0];
            
            // Create the special marker attribute.
            var markerAttribute =
                SF.Attribute(typeof(GrainReferenceAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(
                            SF.TypeOfExpression(grainType.GetTypeSyntax(includeGenericParameters: false))));
            var attributes = SF.AttributeList()
                .AddAttributes(
                    CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                    SF.Attribute(typeof(SerializableAttribute).GetNameSyntax()),
#if !NETSTANDARD_TODO
                    //ExcludeFromCodeCoverageAttribute became an internal class in netstandard
                    SF.Attribute(typeof(ExcludeFromCodeCoverageAttribute).GetNameSyntax()),
#endif
                    markerAttribute);

            var className = CodeGeneratorCommon.ClassPrefix + TypeUtils.GetSuitableClassName(grainType) + ClassSuffix;
            var classDeclaration =
                SF.ClassDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                    .AddBaseListTypes(
                        SF.SimpleBaseType(typeof(GrainReference).GetTypeSyntax()),
                        SF.SimpleBaseType(grainType.GetTypeSyntax()))
                    .AddConstraintClauses(grainType.GetTypeConstraintSyntax())
                    .AddMembers(GenerateConstructors(className))
                    .AddMembers(
                        GenerateInterfaceIdProperty(grainType),
                        GenerateInterfaceNameProperty(grainType),
                        GenerateIsCompatibleMethod(grainType),
                        GenerateGetMethodNameMethod(grainType))
                    .AddMembers(GenerateInvokeMethods(grainType, onEncounteredType))
                    .AddAttributeLists(attributes);
            if (genericTypes.Length > 0)
            {
                classDeclaration = classDeclaration.AddTypeParameterListParameters(genericTypes);
            }

            return classDeclaration;
        }

        /// <summary>
        /// Generates constructors.
        /// </summary>
        /// <param name="className">The class name.</param>
        /// <returns>Constructor syntax for the provided class name.</returns>
        private static MemberDeclarationSyntax[] GenerateConstructors(string className)
        {
            var baseConstructors =
                typeof(GrainReference).GetTypeInfo().GetConstructors(
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Where(_ => !_.IsPrivate);
            var constructors = new List<MemberDeclarationSyntax>();
            foreach (var baseConstructor in baseConstructors)
            {
                var args = baseConstructor.GetParameters()
                    .Select(arg => SF.Argument(arg.Name.ToIdentifierName()))
                    .ToArray();
                var declaration =
                    baseConstructor.GetDeclarationSyntax(className)
                        .WithInitializer(
                            SF.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer)
                                .AddArgumentListArguments(args))
                        .AddBodyStatements();
                constructors.Add(declaration);
            }

            return constructors.ToArray();
        }

        /// <summary>
        /// Generates invoker methods.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="onEncounteredType">
        /// The callback which is invoked when a type is encountered.
        /// </param>
        /// <returns>Invoker methods for the provided grain type.</returns>
        private static MemberDeclarationSyntax[] GenerateInvokeMethods(Type grainType, Action<Type> onEncounteredType)
        {
            var baseReference = SF.BaseExpression();
            var methods = GrainInterfaceUtils.GetMethods(grainType);
            var members = new List<MemberDeclarationSyntax>();
            foreach (var method in methods)
            {
                onEncounteredType(method.ReturnType);
                var methodId = GrainInterfaceUtils.ComputeMethodId(method);
                var methodIdArgument =
                    SF.Argument(SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(methodId)));

                // Construct a new object array from all method arguments.
                var parameters = method.GetParameters();
                var body = new List<StatementSyntax>();
                foreach (var parameter in parameters)
                {
                    onEncounteredType(parameter.ParameterType);
                    if (typeof(IGrainObserver).GetTypeInfo().IsAssignableFrom(parameter.ParameterType))
                    {
                        body.Add(
                            SF.ExpressionStatement(
                                CheckGrainObserverParamInternalExpression.Invoke()
                                    .AddArgumentListArguments(SF.Argument(parameter.Name.ToIdentifierName()))));
                    }
                }
                
                // Get the parameters argument value.
                ExpressionSyntax args;
                if (method.IsGenericMethodDefinition)
                {
                    // Create an arguments array which includes the method's type parameters followed by the method's parameter list.
                    var allParameters = new List<ExpressionSyntax>();
                    foreach (var typeParameter in method.GetGenericArguments())
                    {
                        allParameters.Add(SF.TypeOfExpression(typeParameter.GetTypeSyntax()));
                    }

                    allParameters.AddRange(parameters.Select(GetParameterForInvocation));

                    args =
                        SF.ArrayCreationExpression(typeof(object).GetArrayTypeSyntax())
                        .WithInitializer(
                            SF.InitializerExpression(SyntaxKind.ArrayInitializerExpression)
                              .AddExpressions(allParameters.ToArray()));
                }
                else if (parameters.Length == 0)
                {
                    args = SF.LiteralExpression(SyntaxKind.NullLiteralExpression);
                }
                else
                {
                    args =
                        SF.ArrayCreationExpression(typeof(object).GetArrayTypeSyntax())
                            .WithInitializer(
                                SF.InitializerExpression(SyntaxKind.ArrayInitializerExpression)
                                    .AddExpressions(parameters.Select(GetParameterForInvocation).ToArray()));
                }

                var options = GetInvokeOptions(method);

                // Construct the invocation call.
                if (method.ReturnType == typeof(void))
                {
                    var invocation = SF.InvocationExpression(baseReference.Member("InvokeOneWayMethod"))
                        .AddArgumentListArguments(methodIdArgument)
                        .AddArgumentListArguments(SF.Argument(args));

                    if (options != null)
                    {
                        invocation = invocation.AddArgumentListArguments(options);
                    }

                    body.Add(SF.ExpressionStatement(invocation));
                }
                else
                {
                    var returnType = method.ReturnType == typeof(Task)
                                         ? typeof(object)
                                         : method.ReturnType.GenericTypeArguments[0];
                    var invocation =
                        SF.InvocationExpression(baseReference.Member("InvokeMethodAsync", returnType))
                            .AddArgumentListArguments(methodIdArgument)
                            .AddArgumentListArguments(SF.Argument(args));

                    if (options != null)
                    {
                        invocation = invocation.AddArgumentListArguments(options);
                    }

                    body.Add(SF.ReturnStatement(invocation));
                }

                members.Add(method.GetDeclarationSyntax().AddBodyStatements(body.ToArray()));
            }

            return members.ToArray();
        }

        /// <summary>
        /// Returns syntax for the options argument to <see cref="GrainReference.InvokeMethodAsync{T}"/> and <see cref="GrainReference.InvokeOneWayMethod"/>.
        /// </summary>
        /// <param name="method">The method which an invoke call is being generated for.</param>
        /// <returns>
        /// Argument syntax for the options argument to <see cref="GrainReference.InvokeMethodAsync{T}"/> and
        /// <see cref="GrainReference.InvokeOneWayMethod"/>, or <see langword="null"/> if no options are to be specified.
        /// </returns>
        private static ArgumentSyntax GetInvokeOptions(MethodInfo method)
        {
            var options = new List<ExpressionSyntax>();
            if (GrainInterfaceUtils.IsReadOnly(method))
            {
                options.Add(typeof(InvokeMethodOptions).GetNameSyntax().Member(InvokeMethodOptions.ReadOnly.ToString()));
            }

            if (GrainInterfaceUtils.IsUnordered(method))
            {
                options.Add(typeof(InvokeMethodOptions).GetNameSyntax().Member(InvokeMethodOptions.Unordered.ToString()));
            }

            if (GrainInterfaceUtils.IsAlwaysInterleave(method))
            {
                options.Add(typeof(InvokeMethodOptions).GetNameSyntax().Member(InvokeMethodOptions.AlwaysInterleave.ToString()));
            }

            ExpressionSyntax allOptions;
            if (options.Count <= 1)
            {
                allOptions = options.FirstOrDefault();
            }
            else
            {
                allOptions =
                    options.Aggregate((a, b) => SF.BinaryExpression(SyntaxKind.BitwiseOrExpression, a, b));
            }

            if (allOptions == null)
            {
                return null;
            }

            return SF.Argument(SF.NameColon("options"), SF.Token(SyntaxKind.None), allOptions);
        }

         private static ExpressionSyntax GetParameterForInvocation(ParameterInfo arg, int argIndex)
        {
            var argIdentifier = arg.GetOrCreateName(argIndex).ToIdentifierName();

            // Addressable arguments must be converted to references before passing.
            if (typeof(IAddressable).GetTypeInfo().IsAssignableFrom(arg.ParameterType)
                && arg.ParameterType.GetTypeInfo().IsInterface)
            {
                return
                    SF.ConditionalExpression(
                        SF.BinaryExpression(SyntaxKind.IsExpression, argIdentifier, typeof(Grain).GetTypeSyntax()),
                        SF.InvocationExpression(argIdentifier.Member("AsReference", arg.ParameterType)),
                        argIdentifier);
            }

            return argIdentifier;
        }

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
                    .AddModifiers(SF.Token(SyntaxKind.ProtectedKeyword), SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MemberDeclarationSyntax GenerateIsCompatibleMethod(Type grainType)
        {
            var method = TypeUtils.Method((GrainReference _) => _.IsCompatible(default(int)));
            var methodDeclaration = method.GetDeclarationSyntax();
            var interfaceIdParameter = method.GetParameters()[0].Name.ToIdentifierName();

            var interfaceIds =
                new HashSet<int>(
                    new[] { GrainInterfaceUtils.GetGrainInterfaceId(grainType) }.Concat(
                        GrainInterfaceUtils.GetRemoteInterfaces(grainType).Keys));

            var returnValue = default(BinaryExpressionSyntax);
            foreach (var interfaceId in interfaceIds)
            {
                var check = SF.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    interfaceIdParameter,
                    SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(interfaceId)));

                // If this is the first check, assign it, otherwise OR this check with the previous checks.
                returnValue = returnValue == null
                                  ? check
                                  : SF.BinaryExpression(SyntaxKind.LogicalOrExpression, returnValue, check);
            }

            return
                methodDeclaration.AddBodyStatements(SF.ReturnStatement(returnValue))
                    .AddModifiers(SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MemberDeclarationSyntax GenerateInterfaceNameProperty(Type grainType)
        {
            var propertyName = TypeUtils.Member((GrainReference _) => _.InterfaceName);
            var returnValue = grainType.GetParseableName().GetLiteralExpression();
            return
                SF.PropertyDeclaration(typeof(string).GetTypeSyntax(), propertyName.Name)
                    .AddAccessorListAccessors(
                        SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .AddBodyStatements(SF.ReturnStatement(returnValue)))
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MethodDeclarationSyntax GenerateGetMethodNameMethod(Type grainType)
        {
            // Get the method with the correct type.
            var method =
                typeof(GrainReference).GetTypeInfo()
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetMethodName");

            var methodDeclaration =
                method.GetDeclarationSyntax()
                    .AddModifiers(SF.Token(SyntaxKind.OverrideKeyword));
            var parameters = method.GetParameters();

            var interfaceIdArgument = parameters[0].Name.ToIdentifierName();
            var methodIdArgument = parameters[1].Name.ToIdentifierName();

            var interfaceCases = CodeGeneratorCommon.GenerateGrainInterfaceAndMethodSwitch(
                grainType,
                methodIdArgument,
                methodType => new StatementSyntax[] { SF.ReturnStatement(methodType.Name.GetLiteralExpression()) });

            // Generate the default case, which will throw a NotImplementedException.
            var errorMessage = SF.BinaryExpression(
                SyntaxKind.AddExpression,
                "interfaceId=".GetLiteralExpression(),
                interfaceIdArgument);
            var throwStatement =
                SF.ThrowStatement(
                    SF.ObjectCreationExpression(typeof(NotImplementedException).GetTypeSyntax())
                        .AddArgumentListArguments(SF.Argument(errorMessage)));
            var defaultCase = SF.SwitchSection().AddLabels(SF.DefaultSwitchLabel()).AddStatements(throwStatement);
            var interfaceIdSwitch =
                SF.SwitchStatement(interfaceIdArgument).AddSections(interfaceCases.ToArray()).AddSections(defaultCase);

            return methodDeclaration.AddBodyStatements(interfaceIdSwitch);
        }
    }
}
