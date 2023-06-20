namespace Orleans.CodeGenerator.Generators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.SyntaxGeneration;

internal abstract class ParserBase
{
    protected Compilation compilation;
    protected static INamedTypeSymbol generateCodeForDeclaringAssemblyAttribute;


    public ParserBase(Compilation compilation)
    {
        this.compilation = compilation;
    }

    protected INamedTypeSymbol Type(string metadataName)
    {
        var result = compilation.GetTypeByMetadataName(metadataName);
        if (result is null)
        {
            throw new InvalidOperationException("Cannot find type with metadata name " + metadataName);
        }

        return result;
    }


    public abstract IncrementalGeneratorContext Parse(CancellationToken token);

    protected HashSet<IAssemblySymbol> GetExamineAssemblies()
    {
        generateCodeForDeclaringAssemblyAttribute = Type(Constants.GenerateCodeForDeclaringAssemblyAttribute);
        var assembliesToExamine = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
        var compilationAsm = compilation.Assembly;
        ComputeAssembliesToExamine(compilationAsm, assembliesToExamine);
        return assembliesToExamine;
    }

    protected static void ComputeAssembliesToExamine(IAssemblySymbol asm, HashSet<IAssemblySymbol> expandedAssemblies)
    {
        if (!expandedAssemblies.Add(asm))
        {
            return;
        }

        if (!asm.GetAttributes(generateCodeForDeclaringAssemblyAttribute, out var attrs)) return;

        foreach (var attr in attrs)
        {
            var param = attr.ConstructorArguments.First();
            if (param.Kind != TypedConstantKind.Type)
            {
                throw new ArgumentException($"Unrecognized argument type in attribute [{attr.AttributeClass.Name}({param.ToCSharpString()})]");
            }

            var type = (ITypeSymbol)param.Value;

            // Recurse on the assemblies which the type was declared in.
            var declaringAsm = type.OriginalDefinition.ContainingAssembly;
            if (declaringAsm is null)
            {
                var diagnostic = GenerateCodeForDeclaringAssemblyAttribute_NoDeclaringAssembly_Diagnostic.CreateDiagnostic(attr, type);
                throw new OrleansGeneratorDiagnosticAnalysisException(diagnostic);
            }
            else
            {
                ComputeAssembliesToExamine(declaringAsm, expandedAssemblies);
            }
        }
    }

}
