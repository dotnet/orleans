namespace Orleans.Runtime
{
    /// <summary>
    /// Marker interface for addressable endpoints, such as grains, observers, and other system-internal addressable endpoints
    /// </summary>
    [GenerateMethodSerializers(typeof(NewGrainReference))]
    public interface IAddressable
    {
    }
}
