using Orleans.AdoNet.Storage;

namespace Orleans.AdoNet.Core;

internal class DbConstants(char startEscapeIndicator, char endEscapeIndicator, string unionAllSelectTemplate, bool isSynchronousAdoNetImplementation, bool supportsStreamNatively, bool supportsCommandCancellation, ICommandInterceptor commandInterceptor)
{
    /// <summary>
    /// A query template for union all select
    /// </summary>
    public readonly string UnionAllSelectTemplate = unionAllSelectTemplate;

    /// <summary>
    /// Indicates whether the ADO.net provider does only support synchronous operations.
    /// </summary>
    public readonly bool IsSynchronousAdoNetImplementation = isSynchronousAdoNetImplementation;

    /// <summary>
    /// Indicates whether the ADO.net provider does streaming operations natively.
    /// </summary>
    public readonly bool SupportsStreamNatively = supportsStreamNatively;

    /// <summary>
    /// Indicates whether the ADO.net provider supports cancellation of commands.
    /// </summary>
    public readonly bool SupportsCommandCancellation = supportsCommandCancellation;

    /// <summary>
    /// The character that indicates a start escape key for columns and tables that are reserved words.
    /// </summary>
    public readonly char StartEscapeIndicator = startEscapeIndicator;

    /// <summary>
    /// The character that indicates an end escape key for columns and tables that are reserved words.
    /// </summary>
    public readonly char EndEscapeIndicator = endEscapeIndicator;

    public readonly ICommandInterceptor DatabaseCommandInterceptor = commandInterceptor;
}
