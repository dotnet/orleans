namespace Orleans.CodeGenerator.Generators.ApplicationPartsGenerator;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.SyntaxGeneration;

internal partial class MetadataGenerator
{

    internal class Parser : ParserBase
    {

        private static INamedTypeSymbol _applicationPartAttribute;

        private static INamedTypeSymbol _generateCodeForDeclaringAssemblyAttribute;

        private static MetadataGeneratorContext _applicationPartsGeneratorContext;

        public Parser(Compilation compilation) : base(compilation)
        {

            _applicationPartAttribute = Type(Constants.ApplicationPartAttribute);
            _generateCodeForDeclaringAssemblyAttribute = Type(Constants.GenerateCodeForDeclaringAssemblyAttribute);
            _applicationPartsGeneratorContext = new()
            {
                AssemblyName = compilation.AssemblyName,
                TypeManifestProviderAttribute = Type(Constants.TypeManifestProviderAttribute),
                ApplicationPartAttribute = _applicationPartAttribute

            };
        }

        public override IncrementalGeneratorContext Parse(CancellationToken token)
        {
            AddApplicationParts(compilation, token);
            return _applicationPartsGeneratorContext;

        }


        static void AddApplicationParts(Compilation compilation, CancellationToken token)
        {
            var referencedAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var assembliesToExamine = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var compilationAsm = compilation.Assembly;
            ComputeAssembliesToExamine(compilationAsm, assembliesToExamine);
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm)
                {
                    continue;
                }

                if (!referencedAssemblies.Add(asm))
                {
                    continue;
                }

                if (asm.GetAttributes(_applicationPartAttribute, out var attrs))
                {
                    _applicationPartsGeneratorContext.ApplicationParts.Add(asm.MetadataName);
                    foreach (var attr in attrs)
                    {
                        _applicationPartsGeneratorContext.ApplicationParts.Add((string)attr.ConstructorArguments.First().Value);
                    }
                }
            }

        }


        static void ComputeAssembliesToExamine(IAssemblySymbol asm, HashSet<IAssemblySymbol> expandedAssemblies)
        {
            if (!expandedAssemblies.Add(asm))
            {
                return;
            }

            if (!asm.GetAttributes(_generateCodeForDeclaringAssemblyAttribute, out var attrs)) return;

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
}
