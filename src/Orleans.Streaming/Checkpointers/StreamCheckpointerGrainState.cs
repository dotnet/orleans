using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Stores a persistent stream queue checkpoint.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class StreamCheckpointerGrainState
    {
        /// <summary>
        /// Gets or sets the persisted checkpoint.
        /// </summary>
        [Id(0)]
        public string Checkpoint { get; set; } = string.Empty;
    }
}
