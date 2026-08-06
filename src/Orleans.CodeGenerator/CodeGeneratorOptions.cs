using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

public class CodeGeneratorOptions
{
    public const string IdAttribute = "Orleans.IdAttribute";
    public const string AliasAttribute = "Orleans.AliasAttribute";
    public const string ImmutableAttribute = "Orleans.ImmutableAttribute";
    public static readonly IReadOnlyList<string> ConstructorAttributes = ["Orleans.OrleansConstructorAttribute", "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute"];
    public GenerateFieldIds GenerateFieldIds { get; set; }
    public bool GenerateCompatibilityInvokers { get; set; }
}
