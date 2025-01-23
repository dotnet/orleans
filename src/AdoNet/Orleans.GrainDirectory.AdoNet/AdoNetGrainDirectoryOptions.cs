namespace Orleans.GrainDirectory.AdoNet;

/// <summary>
/// Options for the ADO.NET Grain Directory.
/// </summary>
public class AdoNetGrainDirectoryOptions
{
    /// <summary>
    /// Gets or sets the ADO.NET invariant.
    /// </summary>
    public string Invariant { get; set; }

    /// <summary>
    /// Gets or sets the connection string.
    /// </summary>
    [Redact]
    public string ConnectionString { get; set; }
}
