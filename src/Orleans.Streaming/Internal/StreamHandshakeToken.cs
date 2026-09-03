using System;

namespace Orleans.Streams
{
    [Serializable]
    [GenerateSerializer]
    internal abstract class StreamHandshakeToken : IEquatable<StreamHandshakeToken?>
    {
        [Id(0)]
        public StreamSequenceToken? Token { get; private set; }
        
        public static StreamHandshakeToken? CreateStartToken(StreamSequenceToken? token)
        {
            if (token == null) return default;
            return new StartToken {Token = token};
        }

        public static StreamHandshakeToken CreateStartPositionToken(StreamSubscriptionStartPosition startPosition)
        {
            startPosition.Validate();
            return new StartPositionToken(startPosition);
        }

        public static StreamHandshakeToken? CreateDeliveyToken(StreamSequenceToken? token)
        {
            if (token == null) return default;
            return new DeliveryToken {Token = token};
        }

        public bool Equals(StreamHandshakeToken? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            if (other.GetType() != GetType()) return false;
            if (this is StartPositionToken startPositionToken && other is StartPositionToken otherStartPositionToken)
                return startPositionToken.StartPosition == otherStartPositionToken.StartPosition;
            return Equals(Token, other.Token);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((StreamHandshakeToken)obj);
        }

        public override int GetHashCode() => this is StartPositionToken startPositionToken
            ? HashCode.Combine(GetType(), startPositionToken.StartPosition)
            : HashCode.Combine(GetType(), Token);
    }

    [Serializable]
    [GenerateSerializer]
    internal sealed class StartToken : StreamHandshakeToken { }

    [Serializable]
    [GenerateSerializer]
    internal sealed class StartPositionToken : StreamHandshakeToken
    {
        [Id(1)]
        private StreamSubscriptionStartPosition? startPosition;

        public StreamSubscriptionStartPosition StartPosition
            => startPosition ?? StreamSubscriptionStartPosition.EarliestAvailable;

        public StartPositionToken()
        {
        }

        public StartPositionToken(StreamSubscriptionStartPosition startPosition)
        {
            startPosition.Validate();
            this.startPosition = startPosition;
        }
    }
    
    [Serializable]
    [GenerateSerializer]
    internal sealed class DeliveryToken : StreamHandshakeToken { }
}
