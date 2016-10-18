namespace Orleans.Runtime
{
    /// <summary>
    /// Bridge to provide runtime services to Orleans clients, both inside and outside silos.
    /// </summary>
    /// <remarks>
    /// Only one RuntimeClient is permitted per AppDomain.
    /// </remarks>
    internal static class RuntimeClient
    {
        /// <summary>
        /// A reference to the RuntimeClient instance in the current app domain, 
        /// of the appropriate type depending on whether caller is running inside or outside silo.
        /// </summary>
        internal static IRuntimeClient Current { get; set; }
    }
}
