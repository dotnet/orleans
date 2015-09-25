/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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

    using Orleans.Async;
    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Runtime;

    using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Code generator which generates <see cref="IGrainMethodInvoker"/> for grains.
    /// </summary>
    public static class GrainMethodInvokerGenerator
    {
        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "MethodInvoker";

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        /// <param name="grainType">
        /// The grain interface type.
        /// </param>
        /// <returns>
        /// The generated class.
        /// </returns>
        internal static TypeDeclarationSyntax GenerateClass(Type grainType)
        {
            var baseTypes = new List<BaseTypeSyntax> { SF.SimpleBaseType(typeof(IGrainMethodInvoker).GetTypeSyntax()) };

            var genericTypes = grainType.IsGenericTypeDefinition
                                   ? grainType.GetGenericArguments()
                                         .Select(_ => SF.TypeParameter(_.ToString()))
                                         .ToArray()
                                   : new TypeParameterSyntax[0];

            // Create the special method invoker marker attribute.
            var interfaceId = GrainInterfaceData.GetGrainInterfaceId(grainType);
            var interfaceIdArgument = SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(interfaceId));
            var grainTypeArgument = SF.TypeOfExpression(grainType.GetTypeSyntax(includeGenericParameters: false));
            var attributes = new List<AttributeSyntax>
            {
                CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                SF.Attribute(typeof(MethodInvokerAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(grainType.GetParseableName().GetLiteralExpression()),
                        SF.AttributeArgument(interfaceIdArgument),
                        SF.AttributeArgument(grainTypeArgument)),
                SF.Attribute(typeof(ExcludeFromCodeCoverageAttribute).GetNameSyntax())
            };

            var members = new List<MemberDeclarationSyntax>
            {
                GenerateInvokeMethod(grainType),
                GenerateInterfaceIdProperty(grainType)
            };

            // If this is an IGrainExtension, make the generated class implement IGrainExtensionMethodInvoker.
            if (typeof(IGrainExtension).IsAssignableFrom(grainType))
            {
                baseTypes.Add(SF.SimpleBaseType(typeof(IGrainExtensionMethodInvoker).GetTypeSyntax()));
                members.Add(GenerateExtensionInvokeMethod(grainType));
            }

            var classDeclaration =
                SF.ClassDeclaration(
                    CodeGeneratorCommon.ClassPrefix + TypeUtils.GetSuitableClassName(grainType) + ClassSuffix)
                    .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                    .AddBaseListTypes(baseTypes.ToArray())
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
                SF.Literal(GrainInterfaceData.GetGrainInterfaceId(grainType)));
            return
                SF.PropertyDeclaration(typeof(int).GetTypeSyntax(), property.Name)
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
                    x.Invoke(default(IAddressable), default(int), default(int), default(object[])));

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
                    x.Invoke(default(IGrainExtension), default(int), default(int), default(object[])));

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
            var methodDeclaration = invokeMethod.GetDeclarationSyntax();
            var parameters = invokeMethod.GetParameters();

            var grainArgument = parameters[0].Name.ToIdentifierName();
            var interfaceIdArgument = parameters[1].Name.ToIdentifierName();
            var methodIdArgument = parameters[2].Name.ToIdentifierName();
            var argumentsArgument = parameters[3].Name.ToIdentifierName();

            var interfaceCases = CodeGeneratorCommon.GenerateGrainInterfaceAndMethodSwitch(
                grainType,
                methodIdArgument,
                methodType => GenerateInvokeForMethod(grainType, grainArgument, methodType, argumentsArgument));

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

            // Wrap everything in a try-catch block.
            var faulted = (Expression<Func<Task<object>>>)(() => TaskUtility.Faulted(null));
            const string Exception = "exception";
            var exception = SF.Identifier(Exception);
            var body =
                SF.TryStatement()
                    .AddBlockStatements(grainArgumentCheck, interfaceIdSwitch)
                    .AddCatches(
                        SF.CatchClause()
                            .WithDeclaration(
                                SF.CatchDeclaration(typeof(Exception).GetTypeSyntax()).WithIdentifier(exception))
                            .AddBlockStatements(
                                SF.ReturnStatement(
                                    faulted.Invoke().AddArgumentListArguments(SF.Argument(SF.IdentifierName(Exception))))));

            return methodDeclaration.AddBodyStatements(body);
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
            IdentifierNameSyntax arguments)
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

            // Invoke the method.
            var grainMethodCall =
                SF.InvocationExpression(castGrain.Member(method.Name))
                    .AddArgumentListArguments(parameters.Select(SF.Argument).ToArray());

            if (method.ReturnType == typeof(void))
            {
                var completed = (Expression<Func<Task<object>>>)(() => TaskUtility.Completed());
                return new StatementSyntax[]
                {
                    SF.ExpressionStatement(grainMethodCall), SF.ReturnStatement(completed.Invoke())
                };
            }

            // The invoke method expects a Task<object>, so we need to upcast the returned value.
            // For methods which do not return a value, the Box extension method returns a meaningless value.
            return new StatementSyntax[]
            {
                SF.ReturnStatement(SF.InvocationExpression(grainMethodCall.Member((Task _) => _.Box())))
            };
        }
    }
}