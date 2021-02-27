using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.SyntaxGeneration
{
    internal static class SyntaxFactoryUtility
    {
        /// <summary>
        /// Returns member access syntax.
        /// </summary>
        /// <param name="instance">
        /// The instance.
        /// </param>
        /// <param name="member">
        /// The member.
        /// </param>
        /// <returns>
        /// The resulting <see cref="MemberAccessExpressionSyntax"/>.
        /// </returns>
        public static MemberAccessExpressionSyntax Member(this ExpressionSyntax instance, string member) => instance.Member(member.ToIdentifierName());

        /// <summary>
        /// Returns member access syntax.
        /// </summary>
        /// <param name="instance">
        /// The instance.
        /// </param>
        /// <param name="member">
        /// The member.
        /// </param>
        /// <returns>
        /// The resulting <see cref="MemberAccessExpressionSyntax"/>.
        /// </returns>
        public static MemberAccessExpressionSyntax Member(this ExpressionSyntax instance, IdentifierNameSyntax member) => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, instance, member);

        public static MemberAccessExpressionSyntax Member(this ExpressionSyntax instance, GenericNameSyntax member) => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, instance, member);

        public static MemberAccessExpressionSyntax Member(
            this ExpressionSyntax instance,
            string member,
            params TypeSyntax[] genericTypes) => instance.Member(
                    member.ToGenericName()
                        .AddTypeArgumentListArguments(genericTypes));

        public static GenericNameSyntax ToGenericName(this string identifier) => GenericName(identifier.ToIdentifier());

        public static ClassDeclarationSyntax AddGenericTypeParameters(
            ClassDeclarationSyntax classDeclaration,
            List<(string Name, ITypeParameterSymbol Parameter)> typeParameters)
        {
            var typeParametersWithConstraints = GetTypeParameterConstraints(typeParameters);
            foreach (var (name, constraints) in typeParametersWithConstraints)
            {
                if (constraints.Count > 0)
                {
                    classDeclaration = classDeclaration.AddConstraintClauses(
                        TypeParameterConstraintClause(name).AddConstraints(constraints.ToArray()));
                }
            }

            if (typeParametersWithConstraints.Count > 0)
            {
                classDeclaration = classDeclaration.WithTypeParameterList(
                    TypeParameterList(SeparatedList(typeParametersWithConstraints.Select(tp => TypeParameter(tp.Name)))));
            }

            return classDeclaration;
        }

        public static List<(string Name, List<TypeParameterConstraintSyntax> Constraints)> GetTypeParameterConstraints(List<(string Name, ITypeParameterSymbol Parameter)> typeParameter)
        {
            var allConstraints = new List<(string, List<TypeParameterConstraintSyntax>)>();
            foreach (var (name, tp) in typeParameter)
            {
                var constraints = new List<TypeParameterConstraintSyntax>();
                if (tp.HasReferenceTypeConstraint)
                {
                    constraints.Add(ClassOrStructConstraint(SyntaxKind.ClassConstraint));
                }

                if (tp.HasValueTypeConstraint)
                {
                    constraints.Add(ClassOrStructConstraint(SyntaxKind.StructConstraint));
                }

                foreach (var c in tp.ConstraintTypes)
                {
                    constraints.Add(TypeConstraint(c.ToTypeSyntax()));
                }

                if (tp.HasConstructorConstraint)
                {
                    constraints.Add(ConstructorConstraint());
                }

                allConstraints.Add((name, constraints));
            }

            return allConstraints;
        }

        public static string GetSanitizedName(IParameterSymbol parameter, int index)
        {
            var parameterName = string.IsNullOrWhiteSpace(parameter.Name) ? "arg" : parameter.Name;
            return string.Format(CultureInfo.InvariantCulture, "{0}{1:G}", parameterName, index);
        }
    }
}