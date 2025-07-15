using System;

namespace Orleans.Streams
{
    [Serializable]
    public class StreamCheckpointerGrainState
    {
        public string Checkpoint { get; set; }
    }
}
