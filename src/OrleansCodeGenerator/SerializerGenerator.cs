namespace Orleans.CodeGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Serialization;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Code generator which generates serializers.
    /// </summary>
    public static class SerializerGenerator
    {
        /// <summary>
        /// The suffix appended to the name of the serializer registration class.
        /// </summary>
        private const string RegistererClassSuffix = "Registerer";

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        /// <param name="types">The types.</param>
        /// <returns>
        /// The generated class.
        /// </returns>
        internal static IEnumerable<TypeDeclarationSyntax> GenerateClass(IEnumerable<Type> types)
        {
            var attributes = new List<AttributeSyntax>
            {
                CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
#if !NETSTANDARD
                SF.Attribute(typeof(ExcludeFromCodeCoverageAttribute).GetNameSyntax()),
#endif
                SF.Attribute(typeof(RegisterSerializerAttribute).GetNameSyntax())
            };

            var className = CodeGeneratorCommon.ClassPrefix + Guid.NewGuid().ToString("N") + RegistererClassSuffix;

            var members = new List<MemberDeclarationSyntax>
            {
                GenerateRegisterMethod(types),
                GenerateConstructor(className)
            };
            
            var classDeclaration =
                SF.ClassDeclaration(className)
                  .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                  .AddAttributeLists(SF.AttributeList().AddAttributes(attributes.ToArray()))
                  .AddMembers(members.ToArray());
            return new List<TypeDeclarationSyntax> { classDeclaration };
#if !NETSTANDARD
#endif
        }

        /// <summary>
        /// Returns syntax for the serializer registration method.
        /// </summary>
        /// <param name="types">The types.</param>
        /// <returns>Syntax for the serializer registration method.</returns>
        private static MemberDeclarationSyntax GenerateRegisterMethod(IEnumerable<Type> types)
        {
            return
                SF.MethodDeclaration(typeof(void).GetTypeSyntax(), "Register")
                  .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                  .AddParameterListParameters()
                  .AddBodyStatements(GenerateRegisterExpression(types));
        }

        private static StatementSyntax GenerateRegisterExpression(IEnumerable<Type> types)
        {
            Expression<Action> register = () => SerializationManager.RegisterAll(default(string[]));
            return SF.ExpressionStatement(
                register.Invoke()
                .AddArgumentListArguments(types.Select(type => SF.Argument(type.AssemblyQualifiedName.GetLiteralExpression())).ToArray()));
        }

        /// <summary>
        /// Returns syntax for the constructor.
        /// </summary>
        /// <param name="className">The name of the class.</param>
        /// <returns>Syntax for the constructor.</returns>
        private static ConstructorDeclarationSyntax GenerateConstructor(string className)
        {
            return
                SF.ConstructorDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters()
                    .AddBodyStatements(
                        SF.ExpressionStatement(
                            SF.InvocationExpression(SF.IdentifierName("Register")).AddArgumentListArguments()));
        }
    }
}
