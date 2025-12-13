using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace Orleans.Analyzers
{
    #nullable disable
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class AbstractPropertiesCannotBeSerializedAnalyzer : DiagnosticAnalyzer
    {
        public const string RuleId = "ORLEANS0006";
        private const string Category = "Usage";
        private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AbstractOrStaticMembersCannotBeSerializedTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AbstractOrStaticMembersCannotBeSerializedMessageFormat), Resources.ResourceManager, typeof(Resources));

        internal static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(context =>
            {
                var idAttribute = context.Compilation.GetTypeByMetadataName("Orleans.IdAttribute");
                var generateSerializerAttributeSymbol = context.Compilation.GetTypeByMetadataName("Orleans.GenerateSerializerAttribute");
                if (idAttribute is null || generateSerializerAttributeSymbol is null)
                {
                    return;
                }

                context.RegisterSymbolStartAction(context =>
                {
                    if (SerializationAttributesHelper.ShouldGenerateSerializer((INamedTypeSymbol)context.Symbol, generateSerializerAttributeSymbol))
                    {
                        context.RegisterOperationAction(context => AnalyzeAttribute(context, idAttribute), OperationKind.Attribute);
                    }
                }, SymbolKind.NamedType);
            });
        }

        private static void AnalyzeAttribute(OperationAnalysisContext context, INamedTypeSymbol idAttribute)
        {
            var attributeOperation = (IAttributeOperation)context.Operation;
            string modifier;
            if (context.ContainingSymbol.IsAbstract)
            {
                modifier = "abstract";
            }
            else if (context.ContainingSymbol.IsStatic)
            {
                modifier = "static";
            }
            else
            {
                return;
            }

            if (attributeOperation.Operation is IObjectCreationOperation objectCreationOperation &&
                idAttribute.Equals(objectCreationOperation.Constructor.ContainingType, SymbolEqualityComparer.Default))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, attributeOperation.Syntax.GetLocation(), context.ContainingSymbol.Name, modifier));
            }
        }
    }
}
