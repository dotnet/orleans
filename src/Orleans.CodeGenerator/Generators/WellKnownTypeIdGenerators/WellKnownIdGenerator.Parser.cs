namespace Orleans.CodeGenerator.Generators.WellKnownIdGenerators;

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;

internal partial class WellKnownIdGenerator
{
    internal class Parser : ParserBase
    {

        private static INamedTypeSymbol _wellKnownIdAttribute;

        private static WellKnownIdGeneratorContext _wellKnownIdContext;
        private ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> _wellKnownIdTypes;

        public Parser(Compilation compilation) : base(compilation)
        {

            _wellKnownIdAttribute = Type(Constants.IdAttribute);
            _wellKnownIdContext = new()
            {
                AssemblyName = compilation.AssemblyName,
            };
        }

        public Parser(Compilation compilation, ImmutableArray<(TypeDeclarationSyntax, SemanticModel)> aliasTypes) : this(compilation)
        {
            _wellKnownIdTypes = aliasTypes;
        }

        public override IncrementalGeneratorContext Parse(CancellationToken token)
        {
            SetContextValuesForCurrentAssembly(token);
            SetContextValuesForDeclaringAssemblies(token);
            return _wellKnownIdContext;
        }

        private void SetContextValuesForDeclaringAssemblies(CancellationToken token)
        {
            var declaringAssemblies = GetDeclaringAssemblies();

            if (!declaringAssemblies.Any()) return;

            foreach (var assembly in declaringAssemblies)
            {
                foreach (var typeSymbol in assembly.GetDeclaredTypes())
                {
                    if (GetWellKnownTypeId(typeSymbol) is uint wellKnownTypeId)
                    {
                        _wellKnownIdContext.WellKnownTypeIds.Add((typeSymbol.ToOpenTypeSyntax(), wellKnownTypeId));
                    }
                }
            }


        }

        IncrementalGeneratorContext SetContextValuesForCurrentAssembly(CancellationToken token)
        {
            foreach (var type in _wellKnownIdTypes)
            {
                var symbol = (ITypeSymbol)type.Item2.GetDeclaredSymbol(type.Item1);
                if (GetWellKnownTypeId(symbol) is uint wellKnownTypeId)
                {
                    _wellKnownIdContext.WellKnownTypeIds.Add((symbol.ToOpenTypeSyntax(), wellKnownTypeId));
                }
            }
            return _wellKnownIdContext;
        }

        internal static uint? GetWellKnownTypeId(ISymbol memberSymbol)
        {
            return memberSymbol.GetAttribute(_wellKnownIdAttribute) is { } attr
                ? (uint)attr.ConstructorArguments.First().Value
                : null;
        }
    }
}
