namespace Orleans.Runtime
{
    /// <summary>
    /// Marker interface for grain extensions, used by internal runtime extension endpoints
    /// </summary>
    public interface IGrainExtension : IAddressable
    {
    }
}
