namespace Orleans.SqlUtils.StorageProvider
{
    /// <summary>
    /// Factory interface for creating grain state maps for SQL storage provider
    /// </summary>
    internal interface IGrainStateMapFactory
    {
        /// <summary>
        /// Creates a grain state map 
        /// </summary>
        /// <returns>Grain state map</returns>
        GrainStateMap CreateGrainStateMap();
    }
}