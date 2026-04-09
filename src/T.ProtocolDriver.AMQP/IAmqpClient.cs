using System;
using System.Collections.Concurrent;

namespace T.ProtocolDriver.AMQP
{
    /// <summary>
    /// Common interface for AMQP client implementations (0-9-1 and 1.0).
    /// </summary>
    internal interface IAmqpClient : IDisposable
    {
        /// <summary>
        /// Whether the client is currently connected to the broker.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Connect to the AMQP broker.
        /// </summary>
        void Connect(AmqpConnectionSettings settings);

        /// <summary>
        /// Disconnect from the AMQP broker.
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Subscribe to an address/routing key. Incoming messages are placed into the receive queue.
        /// </summary>
        void Subscribe(string address, ConcurrentQueue<AmqpMessage> receiveQueue);

        /// <summary>
        /// Unsubscribe from an address/routing key.
        /// </summary>
        void Unsubscribe(string address);

        /// <summary>
        /// Publish a message to the specified address/routing key.
        /// </summary>
        void Publish(string address, byte[] payload, bool retained);

        /// <summary>
        /// Declare exchange and queue bindings (AMQP 0-9-1) or create links (AMQP 1.0).
        /// </summary>
        void DeclareEndpoint(string address);
    }

    /// <summary>
    /// Connection settings parsed from station configuration.
    /// </summary>
    internal class AmqpConnectionSettings
    {
        public string BrokerUrl { get; set; } = AmqpConstants.DefaultBrokerUrl;
        public int Port { get; set; } = AmqpConstants.DefaultPort091;
        public string VirtualHost { get; set; } = AmqpConstants.DefaultVirtualHost;
        public string ClientId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public string NetworkSecurity { get; set; } = AmqpConstants.SecurityNone;
        public string X509CertificatePath { get; set; } = "";
        public string Qos { get; set; } = AmqpConstants.QosAtLeastOnce;
        public int KeepAlive { get; set; } = AmqpConstants.DefaultKeepAlive;
        public string Exchange { get; set; } = AmqpConstants.DefaultExchange;
        public string ExchangeType { get; set; } = AmqpConstants.DefaultExchangeType;
        public string ContainerId { get; set; } = "";
        public string AmqpVersion { get; set; } = AmqpConstants.Version091;
        public string PayloadFormat { get; set; } = AmqpConstants.PayloadJson;
    }

    /// <summary>
    /// Represents a received AMQP message.
    /// </summary>
    internal class AmqpMessage
    {
        public string Address { get; set; }
        public byte[] Payload { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
