using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    internal interface IStreamSubscriptionHandle
    {
        Task<StreamHandshakeToken> DeliverItem(object item, StreamSequenceToken currentToken, StreamHandshakeToken handshakeToken);
        Task<StreamHandshakeToken> DeliverBatch(IBatchContainer item, StreamHandshakeToken handshakeToken);
        Task CompleteStream();
        Task ErrorInStream(Exception exc);
        StreamHandshakeToken GetSequenceToken();
    }
}