namespace Orleans.Providers
{
    /// <summary>
    /// Marker interface to be implemented by any app bootstrap classes that want to be loaded and auto-run during silo startup
    /// </summary>
    public interface IBootstrapProvider : IProvider
    {
    }
}
