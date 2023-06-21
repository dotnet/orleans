namespace Orleans.CodeGenerator.Generators.MetadataGenerators;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.SyntaxGeneration;

internal partial class MetadataGenerator
{

    internal class Parser : ParserBase
    {

        private static INamedTypeSymbol _applicationPartAttribute;


        private static MetadataGeneratorContext _applicationPartsGeneratorContext;

        public Parser(Compilation compilation) : base(compilation)
        {

            _applicationPartAttribute = Type(Constants.ApplicationPartAttribute);
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


        void AddApplicationParts(Compilation compilation, CancellationToken token)
        {
            var referencedAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var compilationAsm = compilation.Assembly;
            referencedAssemblies.Add(compilationAsm);
            _applicationPartsGeneratorContext.ApplicationParts.Add(compilationAsm.MetadataName);
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



    }
}
