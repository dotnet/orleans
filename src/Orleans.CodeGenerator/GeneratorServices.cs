using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator;

internal interface IGeneratorServices
{
    Compilation Compilation { get; }
    CodeGeneratorOptions Options { get; }
    LibraryTypes LibraryTypes { get; }
}

internal sealed class GeneratorServices(Compilation compilation, CodeGeneratorOptions options, LibraryTypes libraryTypes) : IGeneratorServices
{
    public GeneratorServices(Compilation compilation, CodeGeneratorOptions options)
        : this(compilation, options, LibraryTypes.FromCompilation(compilation, options))
    {
    }

    public Compilation Compilation { get; } = compilation ?? throw new ArgumentNullException(nameof(compilation));
    public CodeGeneratorOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));
    public LibraryTypes LibraryTypes { get; } = libraryTypes ?? throw new ArgumentNullException(nameof(libraryTypes));
}
