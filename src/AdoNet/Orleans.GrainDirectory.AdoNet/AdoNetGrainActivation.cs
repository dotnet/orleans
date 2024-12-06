namespace Orleans.GrainDirectory.AdoNet;

/// <summary>
/// The model that represents a grain activation in an ADONET grain directory.
/// </summary>
internal record AdoNetGrainActivation(
    string ClusterId,
    string GrainId,
    string SiloAddress,
    string ActivationId)
{
    public AdoNetGrainActivation() : this("", "", "", "")
    {
    }
}
