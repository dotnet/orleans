namespace Orleans
{
    /// <summary>Defines the state of a grain</summary>
    public interface IGrainState
    {
        /// <summary>The application level payload that is the actual state.</summary>
        object State { get; set; }

        /// <summary>An e-tag that allows optimistic concurrency checks at the storage provider level.</summary>
        string ETag { get; set; }
    }
}
