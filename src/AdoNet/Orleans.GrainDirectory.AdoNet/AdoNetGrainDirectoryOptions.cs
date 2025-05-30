namespace Orleans.GrainDirectory.AdoNet;

/// <summary>
/// Options for the ADO.NET Grain Directory.
/// </summary>
public class AdoNetGrainDirectoryOptions
{
    /// <summary>
    /// Gets or sets the ADO.NET invariant.
    /// </summary>
    [Required]
    public string Invariant { get; set; } = "";

    /// <summary>
    /// Gets or sets the connection string.
    /// </summary>
    [Redact]
    [Required]
    public string ConnectionString { get; set; } = "";
}
