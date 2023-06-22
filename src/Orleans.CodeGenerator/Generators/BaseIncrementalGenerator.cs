namespace Orleans.CodeGenerator.Generators;

using System;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Diagnostics;

internal abstract class BaseIncrementalGenerator : IIncrementalGenerator
{

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {

        context.RegisterPostInitializationOutput(PostInitializationOutputCallback);

        AddSyntaxProvider(context.SyntaxProvider);

        var incrementalGeneratorContext = Execute(context);


        context.RegisterSourceOutput(incrementalGeneratorContext, RegisterSourceOutputPrivate);
    }

    protected virtual void PostInitializationOutputCallback(IncrementalGeneratorPostInitializationContext context) { }

    private void RegisterSourceOutputPrivate(SourceProductionContext context, IncrementalGeneratorContext igContext)
    {

        try
        {
            RegisterSourceOutput(context, igContext);
        }
        catch (Exception exception) when (HandleException(context, exception))
        {
        }
    }

    protected abstract void RegisterSourceOutput(SourceProductionContext context, IncrementalGeneratorContext igContext);
    protected abstract IncrementalValueProvider<IncrementalGeneratorContext> Execute(IncrementalGeneratorInitializationContext context);
    protected abstract void AddSyntaxProvider(SyntaxValueProvider syntaxProvider);

    private static bool HandleException(SourceProductionContext context, Exception exception)
    {
        if (exception is OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            context.ReportDiagnostic(analysisException.Diagnostic);
            return true;
        }

        context.ReportDiagnostic(UnhandledCodeGenerationExceptionDiagnostic.CreateDiagnostic(exception));
        return false;
    }


}
