namespace Orleans.Runtime
{
    /// <summary>
    /// Marker interface for grain extensions, used by internal runtime extension endpoints
    /// </summary>
    [GenerateMethodSerializers(typeof(GrainReference), isExtension: true)]
    public interface IGrainExtension : IAddressable
    {
    }
}
