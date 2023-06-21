namespace Orleans.CodeGenerator.Generators.CompoundAliasGenerators;
internal class CompoundAliasGeneratorContext : IncrementalGeneratorContext
{
    public CompoundTypeAliasTree CompoundTypeAliases { get; } = CompoundTypeAliasTree.Create();

}
