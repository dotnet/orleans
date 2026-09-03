using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

/// <summary>
/// Configures serializer and RPC code generation.
/// </summary>
public class CodeGeneratorOptions
{
    /// <summary>
    /// The metadata name of <c>Orleans.IdAttribute</c>.
    /// </summary>
    public const string IdAttribute = "Orleans.IdAttribute";

    /// <summary>
    /// The metadata name of <c>Orleans.AliasAttribute</c>.
    /// </summary>
    public const string AliasAttribute = "Orleans.AliasAttribute";

    /// <summary>
    /// The metadata name of <c>Orleans.ImmutableAttribute</c>.
    /// </summary>
    public const string ImmutableAttribute = "Orleans.ImmutableAttribute";

    /// <summary>
    /// The metadata names of attributes which identify the constructor used during deserialization.
    /// </summary>
    public static readonly IReadOnlyList<string> ConstructorAttributes = ["Orleans.OrleansConstructorAttribute", "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute"];

    /// <summary>
    /// Gets or sets the strategy used to generate field identifiers for serializable members.
    /// </summary>
    public GenerateFieldIds GenerateFieldIds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether compatibility invokers are generated for inherited RPC methods.
    /// </summary>
    public bool GenerateCompatibilityInvokers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether generated serializers and copiers support adding members using .NET Hot Reload.
    /// </summary>
    public bool HotReloadSafe { get; set; }
}
