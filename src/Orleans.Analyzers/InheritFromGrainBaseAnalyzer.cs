using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class InheritFromGrainBaseAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ORLEANS0003";
        private const string BaseInterfaceName = "Orleans.IGrain";
        private const string BaseClassName = "Orleans.Grain";
        public const string Title = "Non-abstract classes that implement IGrain should derive from the base class Orleans.Grain";
        public const string MessageFormat = Title;
        private const string Category = "Usage";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(Rule); } }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(AnalyzeSymbol, SymbolKind.NamedType);
        }

        private static void AnalyzeSymbol(SymbolAnalysisContext context)
        {
            var namedSymbol = context.Symbol as INamedTypeSymbol;

            // continue if the class is not abstract.
            if (namedSymbol == null || namedSymbol.IsAbstract) return;

            // continue only if there is no issue inside the class.
            var diagnostics = context.Compilation.GetDeclarationDiagnostics();
            if (diagnostics.Any()) return;

            // continue only if the class implements IGrain
            var GrainInterfaceType = context.Compilation.GetTypeByMetadataName(BaseInterfaceName);
            if (GrainInterfaceType == null || !namedSymbol.AllInterfaces.Contains(GrainInterfaceType)) return;

            // Get the base type of the class
            var GrainType = context.Compilation.GetTypeByMetadataName(BaseClassName);
            var baseType = namedSymbol.BaseType;
            
            // Check equality with the base class
            if (!SymbolEqualityComparer.Default.Equals(baseType, GrainType))
            {
                var location = namedSymbol.Locations.FirstOrDefault();

                if (location != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule, location));
                }
            }
        }
    }
}
