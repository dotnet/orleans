namespace Orleans.Runtime
{
    /// <summary>
    /// Details of the local silo.
    /// </summary>
    internal interface ILocalSiloDetails
    {
        /// <summary>
        /// Gets the address of this silo's inter-silo endpoint.
        /// </summary>
        SiloAddress SiloAddress { get; }
    }
}