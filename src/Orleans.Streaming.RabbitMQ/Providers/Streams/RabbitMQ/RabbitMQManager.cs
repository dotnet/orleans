using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Orleans.Providers.RabbitMQ.Streams.RabbitMQ
{
    public class RabbitMQManager : IDisposable
    {
        private RabbitMQOptions _rabbitFact;
        private IConnection _rabbitConn;
        private IModel _model;
        private readonly ILogger _logger;
        public RabbitMQManager(RabbitMQOptions rabbitMQOptions, ILogger logger)

        {
            _rabbitFact = rabbitMQOptions ?? throw new ArgumentNullException(nameof(rabbitMQOptions));
            _logger = logger;
            try
            {
                _rabbitConn = _rabbitFact.CreateConnection();
            }
            catch (Exception ex)
            {
                throw new ConnectFailureException("connection to rabbitmq is not open", ex.InnerException);
            }
            _model = _rabbitConn.CreateModel();
            _model.ExchangeDeclare(_rabbitFact.ExchangeOptions.Name,
                                   _rabbitFact.ExchangeOptions.ExchangeType,
                                   _rabbitFact.ExchangeOptions.Durable,
                                   _rabbitFact.ExchangeOptions.AutoDelete,
                                   _rabbitFact.ExchangeOptions.Arguments);
            _model.QueueDeclare(_rabbitFact.QueueOptions.Queue,
                                _rabbitFact.QueueOptions.Durable,
                                _rabbitFact.QueueOptions.Exclusive,
                                _rabbitFact.QueueOptions.AutoDelete,
                                _rabbitFact.QueueOptions.Arguments);
            _model.QueueBind(_rabbitFact.BindingOptions.Queue,
                             _rabbitFact.BindingOptions.Exchange,
                             _rabbitFact.BindingOptions.RoutingKey,
                             _rabbitFact.BindingOptions.Arguments);
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("instantiated rabbitmq client and queue");
            }
        }

        public void PublishMessage(RabbitMQMessage msg)
        {
            _model.BasicPublish(_rabbitFact.ExchangeOptions.Name,
                                _rabbitFact.BindingOptions.RoutingKey,
                                msg,
                                msg.Body);
        }

        public RabbitMQMessage ReceiveMessage()
        {
            // we recast it to keep the simple messaging pattern.
            return _model.BasicGet(_rabbitFact.BindingOptions.Queue, true) as RabbitMQMessage;
        }

        public void Dispose()
        {
            _model.Close();
            _model.Dispose();
            _rabbitConn.Close();
            _rabbitConn.Dispose();
        }
    }

    /// <summary>
    /// RabbitMQMessage is our vehicle to represent a message for both publishing and consumption.
    /// </summary>
    /// <inheritdoc/>
    public class RabbitMQMessage : BasicGetResult, IBasicProperties
    {
        public RabbitMQMessage() : base(0, false, "", "", 0, null, null) { }

        public RabbitMQMessage(ulong deliveryTag,
                             bool redelivered,
                             string exchange,
                             string routingKey,
                             uint messageCount,
                             IBasicProperties basicProperties,
                             byte[] body) :
            base(deliveryTag,
                 redelivered,
                 exchange,
                 routingKey,
                 messageCount,
                 basicProperties,
                 body)
        { }

        /// <inheritdoc/>
        public string AppId { get; set; }

        /// <summary>
        /// Message body.
        /// </summary>
        public new byte[] Body;

        /// <inheritdoc/>
        public string ClusterId { get; set; }

        /// <inheritdoc/>
        public string ContentEncoding { get; set; }

        /// <inheritdoc/>
        public string ContentType { get; set; }

        /// <inheritdoc/>
        public string CorrelationId { get; set; }

        /// <inheritdoc/>
        public byte DeliveryMode { get; set; }

        /// <inheritdoc/>
        public string Expiration { get; set; }

        /// <inheritdoc/>
        public IDictionary<string, object> Headers { get; set; }

        /// <inheritdoc/>
        public string MessageId { get; set; }

        /// <inheritdoc/>
        public bool Persistent { get; set; }

        /// <inheritdoc/>
        public byte Priority { get; set; }

        /// <inheritdoc/>
        public string ReplyTo { get; set; }

        /// <inheritdoc/>
        public PublicationAddress ReplyToAddress { get; set; }

        /// <inheritdoc/>
        public AmqpTimestamp Timestamp { get; set; }

        /// <inheritdoc/>
        public string Type { get; set; }

        /// <inheritdoc/>
        public string UserId { get; set; }

        /// <inheritdoc/>
        public int ProtocolClassId => throw new NotImplementedException();

        /// <inheritdoc/>
        public string ProtocolClassName => throw new NotImplementedException();

        // notes (mxplusb): the rabbitmq client requries the below methods be implemented
        // by implementers, but the core client contains no reference implementations. these
        // are solely here to satisfy the interface requirement.

        public void ClearAppId()
        {
            throw new NotImplementedException();
        }

        public void ClearClusterId()
        {
            throw new NotImplementedException();
        }

        public void ClearContentEncoding()
        {
            throw new NotImplementedException();
        }

        public void ClearContentType()
        {
            throw new NotImplementedException();
        }

        public void ClearCorrelationId()
        {
            throw new NotImplementedException();
        }

        public void ClearDeliveryMode()
        {
            throw new NotImplementedException();
        }

        public void ClearExpiration()
        {
            throw new NotImplementedException();
        }

        public void ClearHeaders()
        {
            throw new NotImplementedException();
        }

        public void ClearMessageId()
        {
            throw new NotImplementedException();
        }

        public void ClearPriority()
        {
            throw new NotImplementedException();
        }

        public void ClearReplyTo()
        {
            throw new NotImplementedException();
        }

        public void ClearTimestamp()
        {
            throw new NotImplementedException();
        }

        public void ClearType()
        {
            throw new NotImplementedException();
        }

        public void ClearUserId()
        {
            throw new NotImplementedException();
        }

        public bool IsAppIdPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsClusterIdPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsContentEncodingPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsContentTypePresent()
        {
            throw new NotImplementedException();
        }

        public bool IsCorrelationIdPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsDeliveryModePresent()
        {
            throw new NotImplementedException();
        }

        public bool IsExpirationPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsHeadersPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsMessageIdPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsPriorityPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsReplyToPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsTimestampPresent()
        {
            throw new NotImplementedException();
        }

        public bool IsTypePresent()
        {
            throw new NotImplementedException();
        }

        public bool IsUserIdPresent()
        {
            throw new NotImplementedException();
        }

        public void SetPersistent(bool persistent)
        {
            throw new NotImplementedException();
        }
    }
}
