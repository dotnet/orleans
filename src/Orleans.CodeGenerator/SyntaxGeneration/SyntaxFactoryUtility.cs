using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator.SyntaxGeneration;

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

    /// <summary>
    /// Returns each type parameter's spelled constraints, in declaration order, with type parameters renamed to their generated names.
    /// </summary>
    public static List<(string Name, List<string> Constraints)> GetTypeParameterConstraintTexts(List<(string Name, ITypeParameterSymbol Parameter)> typeParameters)
    {
        var allConstraints = new List<(string, List<string>)>(typeParameters.Count);
        var substitutions = new Dictionary<ITypeParameterSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var (name, parameter) in typeParameters)
        {
            substitutions[parameter] = name;
        }

        foreach (var (name, tp) in typeParameters)
        {
            var constraints = new List<string>();
            if (tp.HasUnmanagedTypeConstraint)
            {
                constraints.Add("unmanaged");
            }
            else if (tp.HasValueTypeConstraint)
            {
                constraints.Add("struct");
            }
            else if (tp.HasNotNullConstraint)
            {
                constraints.Add("notnull");
            }
            else if (tp.HasReferenceTypeConstraint)
            {
                constraints.Add("class");
            }

            foreach (var c in tp.ConstraintTypes)
            {
                constraints.Add(CodeText.Type(c.ToTypeSyntax(substitutions)));
            }

            if (tp.HasConstructorConstraint)
            {
                constraints.Add("new()");
            }

            allConstraints.Add((name, constraints));
        }

        return allConstraints;
    }
}
