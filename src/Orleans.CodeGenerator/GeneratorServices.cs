using System;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator
{
    internal interface IGeneratorServices
    {
        Compilation Compilation { get; }
        CodeGeneratorOptions Options { get; }
        LibraryTypes LibraryTypes { get; }
    }

    internal sealed class GeneratorServices : IGeneratorServices
    {
        public GeneratorServices(Compilation compilation, CodeGeneratorOptions options)
        {
            Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            LibraryTypes = LibraryTypes.FromCompilation(compilation, options);
        }

        public Compilation Compilation { get; }
        public CodeGeneratorOptions Options { get; }
        public LibraryTypes LibraryTypes { get; }
    }
}
