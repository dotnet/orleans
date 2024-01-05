using Orleans.Streams;

namespace TestGrains
{
    [Serializable]
    [Orleans.GenerateSerializer]
    public class StreamCheckpoint<TState>
    {
        [Orleans.Id(0)]
        public Guid StreamGuid { get; set; }
        [Orleans.Id(1)]
        public string StreamNamespace { get; set; }
        [Orleans.Id(2)]
        public StreamSequenceToken StartToken { get; set; }
        [Orleans.Id(3)]
        public StreamSequenceToken LastProcessedToken { get; set; }
        [Orleans.Id(4)]
        public TState Accumulator { get; set; }

        public StreamSequenceToken RecoveryToken { get { return LastProcessedToken ?? StartToken; } }

        public bool IsDuplicate(StreamSequenceToken sequenceToken)
        {
            // This is the first event, so it can't be a duplicate
            if (StartToken == null)
                return false;

            // if we have processed events, compare with the sequence token of last event we processed.
            if (LastProcessedToken != null)
            {
                // if Last processed is not older than this sequence token, then this token is a duplicate
                return !LastProcessedToken.Older(sequenceToken);
            }

            // If all we have is the start token, then we've not processed the first event, so we should process any event at or after the start token.
            return StartToken.Newer(sequenceToken);
        }

        public bool TryUpdateStartToken(StreamSequenceToken sequenceToken)
        {
            if (StartToken == null)
            {
                StartToken = sequenceToken;
                return true;
            }
            return false;
        }
    }
}
