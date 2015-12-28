using System;

namespace Orleans.Streams
{
    [Serializable]
    internal class StreamHandshakeToken : IEquatable<StreamHandshakeToken>
    {
        public StreamSequenceToken Token { get; private set; }
        
        public static StreamHandshakeToken CreateStartToken(StreamSequenceToken token)
        {
            if (token == null) return default(StreamHandshakeToken);
            return new StartToken {Token = token};
        }

        public static StreamHandshakeToken CreateDeliveyToken(StreamSequenceToken token)
        {
            if (token == null) return default(StreamHandshakeToken);
            return new DeliveryToken {Token = token};
        }

        public bool Equals(StreamHandshakeToken other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            if (other.GetType() != GetType()) return false;
            return Equals(Token, other.Token);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((StreamHandshakeToken)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (GetType().GetHashCode() * 397) ^ (Token != null ? Token.GetHashCode() : 0);
            }
        }
    }

    [Serializable]
    internal class StartToken : StreamHandshakeToken { }
    
    [Serializable]
    internal class DeliveryToken : StreamHandshakeToken { }
}
