using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Orleans.CodeGenerator
{
    public interface IGeneratorExecutionContext
    {
        Compilation Compilation { get; }
        CancellationToken CancellationToken { get; }
        void ReportDiagnostic(Diagnostic diagnostic);
    }

    internal class SourceGeneratorExecutionContext : IGeneratorExecutionContext
    {
        private readonly GeneratorExecutionContext _context;

        public SourceGeneratorExecutionContext(GeneratorExecutionContext context) => _context = context;

        public Compilation Compilation => _context.Compilation;

        public CancellationToken CancellationToken => _context.CancellationToken;

        public void ReportDiagnostic(Diagnostic diagnostic) => _context.ReportDiagnostic(diagnostic);
    }

    [Generator]
    public class OrleansSourceGenerator : ISourceGenerator
    {
        private static readonly int[] SuppressCompilerWarnings =
        {
            162, // CS0162 - Unreachable code detected.
            219, // CS0219 - The variable 'V' is assigned but its value is never used.
            414, // CS0414 - The private field 'F' is assigned but its value is never used.
            618, // CS0616 - Member is obsolete.
            649, // CS0649 - Field 'F' is never assigned to, and will always have its default value.
            693, // CS0693 - Type parameter 'type parameter' has the same name as the type parameter from outer type 'T'
            1591, // CS1591 - Missing XML comment for publicly visible type or member 'Type_or_Member'
            1998 // CS1998 - This async method lacks 'await' operators and will run synchronously
        };

        public void Execute(GeneratorExecutionContext context)
        {
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_debugsourcegenerator", out var attachDebuggerValue)
                && string.Equals("true", attachDebuggerValue, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debugger.Launch();
            }

            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue("build_property.orleans_designtimebuild", out var isDesignTimeBuild)
                && string.Equals("true", isDesignTimeBuild, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                ExecuteInner(context);
            }
            catch (Exception exception)
            {
                // This is temporary till https://github.com/dotnet/roslyn/issues/46084 is fixed
                context.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "ORLEANS0000",
                        "An exception was thrown by the Orleans generator",
                        "An exception was thrown by the Orleans generator: '{0}'",
                        "Orleans",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    Location.None,
                    exception.ToString()));
            }
        }

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ExecuteInner(GeneratorExecutionContext context)
        {
            var codeGenerator = new CodeGenerator(new SourceGeneratorExecutionContext(context), new CodeGeneratorOptions());
            var syntax = codeGenerator.GenerateCode(context.CancellationToken);

            var sourceBuilder = new StringBuilder();
            foreach (var warningNum in SuppressCompilerWarnings)
            {
                sourceBuilder.AppendLine($"#pragma warning disable {warningNum}");
            }

            sourceBuilder.AppendLine(syntax.NormalizeWhitespace().ToFullString());

            foreach (var warningNum in SuppressCompilerWarnings)
            {
                sourceBuilder.AppendLine($"#pragma warning restore {warningNum}");
            }

            var sourceText = SourceText.From(sourceBuilder.ToString(), Encoding.UTF8);
            
            var fileName = context.Compilation.AssemblyName switch
            {
                string name when !string.IsNullOrWhiteSpace(name) => $"{name}.orleans.g.cs",
                _ => "orleans.g.cs"
            };
            context.AddSource(fileName, sourceText);
        }
    }
}
