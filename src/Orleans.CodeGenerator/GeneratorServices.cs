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
            : this(compilation, options, LibraryTypes.FromCompilation(compilation, options))
        {
        }

        public GeneratorServices(Compilation compilation, CodeGeneratorOptions options, LibraryTypes libraryTypes)
        {
            Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            LibraryTypes = libraryTypes ?? throw new ArgumentNullException(nameof(libraryTypes));
        }

        public Compilation Compilation { get; }
        public CodeGeneratorOptions Options { get; }
        public LibraryTypes LibraryTypes { get; }
    }
}
