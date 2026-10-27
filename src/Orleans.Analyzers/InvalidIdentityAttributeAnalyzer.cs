using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.Serialization;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidIdentityAttributeAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0028";

    private static readonly DiagnosticDescriptor Rule = new(
        RuleId,
        new LocalizableResourceString("InvalidIdentityTitle", Resources.ResourceManager, typeof(Resources)),
        new LocalizableResourceString("InvalidIdentityMessageFormat", Resources.ResourceManager, typeof(Resources)),
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: new LocalizableResourceString("InvalidIdentityDescription", Resources.ResourceManager, typeof(Resources)),
        helpLinkUri: Constants.GetDiagnosticHelpLink(RuleId));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(context =>
        {
            var aliasAttribute = context.Compilation.GetTypeByMetadataName(Constants.AliasAttributeFullyQualifiedName);
            var grainTypeAttribute = context.Compilation.GetTypeByMetadataName(Constants.GrainTypeAttributeFullyQualifiedName);
            var grainInterfaceTypeAttribute = context.Compilation.GetTypeByMetadataName(Constants.GrainInterfaceTypeAttributeFullyQualifiedName);

            context.RegisterSyntaxNodeAction(context =>
            {
                var attribute = (AttributeSyntax)context.Node;
                if (context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is not IMethodSymbol constructor
                    || attribute.ArgumentList is not { Arguments.Count: > 0 } arguments)
                {
                    return;
                }

                var attributeType = constructor.ContainingType;
                if (!SymbolEqualityComparer.Default.Equals(attributeType, aliasAttribute)
                    && !SymbolEqualityComparer.Default.Equals(attributeType, grainTypeAttribute)
                    && !SymbolEqualityComparer.Default.Equals(attributeType, grainInterfaceTypeAttribute))
                {
                    return;
                }

                var expression = arguments.Arguments[0].Expression;
                var value = context.SemanticModel.GetConstantValue(expression, context.CancellationToken);
                if (value.HasValue && !IdentityNameValidator.IsValid(value.Value as string))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, expression.GetLocation(), attributeType.Name));
                }
            }, SyntaxKind.Attribute);
        });
    }
}
