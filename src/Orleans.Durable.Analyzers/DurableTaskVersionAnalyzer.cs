using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Orleans.DurableTasks.Analyzers;

/// <summary>Reports version checks whose change name or supported range isn't a constant.</summary>
/// <remarks>
/// <c>DurableTask.GetVersion</c> records an execution's version under the change's name, and a deployment's supported
/// range belongs to the code which branches on it, so both are constants of the calling code.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DurableTaskVersionAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DurableTaskDiagnostics.NonConstantVersion);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            if (DurableTaskSymbols.Create(start.Compilation) is not { } symbols)
            {
                return;
            }

            start.RegisterOperationAction(operation => AnalyzeInvocation(operation, symbols), OperationKind.Invocation);
            if (symbols.DurableVersion is { } durableVersion)
            {
                start.RegisterOperationAction(operation => AnalyzeCreation(operation, durableVersion), OperationKind.ObjectCreation);
            }
        });
    }

    private static void AnalyzeCreation(OperationAnalysisContext context, INamedTypeSymbol durableVersion)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (SymbolEqualityComparer.Default.Equals(creation.Type, durableVersion))
        {
            ReportNonConstantArguments(context, creation.Arguments, "DurableVersion");
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, DurableTaskSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (invocation.TargetMethod is not { Name: "GetVersion", IsStatic: true } method
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType, symbols.DurableTask))
        {
            return;
        }

        ReportNonConstantArguments(context, invocation.Arguments, "DurableTask.GetVersion");
    }

    private static void ReportNonConstantArguments(
        OperationAnalysisContext context,
        ImmutableArray<IArgumentOperation> arguments,
        string target)
    {
        foreach (var argument in arguments)
        {
            if (argument.Parameter is { } parameter && !argument.Value.ConstantValue.HasValue)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DurableTaskDiagnostics.NonConstantVersion,
                    argument.Value.Syntax.GetLocation(),
                    parameter.Name,
                    target));
            }
        }
    }
}
