using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace T.ProtocolDriver.AMQP
{
    /// <summary>
    /// AMQP 0-9-1 client implementation using RabbitMQ.Client.
    /// Manages connection, channel, exchange/queue declaration, publish, and subscribe.
    /// </summary>
    internal class AmqpClient091 : IAmqpClient
    {
        private IConnection _connection;
        private IModel _channel;
        private string _queueName;
        private AmqpConnectionSettings _settings;
        private EventingBasicConsumer _consumer;
        private readonly object _lock = new object();
        private readonly Dictionary<string, ConcurrentQueue<AmqpMessage>> _subscriptions
            = new Dictionary<string, ConcurrentQueue<AmqpMessage>>();
        private bool _disposed;

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return _connection != null && _connection.IsOpen
                        && _channel != null && _channel.IsOpen;
                }
            }
        }

        public void Connect(AmqpConnectionSettings settings)
        {
            lock (_lock)
            {
                _settings = settings;

                var factory = new ConnectionFactory
                {
                    HostName = settings.BrokerUrl,
                    Port = settings.Port,
                    VirtualHost = string.IsNullOrEmpty(settings.VirtualHost) ? "/" : settings.VirtualHost,
                    RequestedHeartbeat = TimeSpan.FromSeconds(settings.KeepAlive > 0 ? settings.KeepAlive : AmqpConstants.DefaultKeepAlive),
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromMilliseconds(AmqpConstants.ReconnectDelayMs)
                };

                if (!string.IsNullOrEmpty(settings.ClientId))
                    factory.ClientProvidedName = settings.ClientId;

                if (!string.IsNullOrEmpty(settings.UserName))
                    factory.UserName = settings.UserName;
                if (!string.IsNullOrEmpty(settings.Password))
                    factory.Password = settings.Password;

                // TLS configuration
                if (settings.NetworkSecurity != AmqpConstants.SecurityNone
                    && !string.IsNullOrEmpty(settings.NetworkSecurity))
                {
                    factory.Ssl = new SslOption
                    {
                        Enabled = true,
                        ServerName = settings.BrokerUrl
                    };

                    switch (settings.NetworkSecurity)
                    {
                        case AmqpConstants.SecurityTls12:
                            factory.Ssl.Version = SslProtocols.Tls12;
                            break;
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER
                        case AmqpConstants.SecurityTls13:
                            factory.Ssl.Version = SslProtocols.Tls13;
                            break;
#endif
                        default:
                            factory.Ssl.Version = SslProtocols.Tls12;
                            break;
                    }

                    if (!string.IsNullOrEmpty(settings.X509CertificatePath))
                    {
                        factory.Ssl.CertPath = settings.X509CertificatePath;
                        factory.Ssl.AcceptablePolicyErrors = SslPolicyErrors.RemoteCertificateNameMismatch;
                    }
                }

                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();

                // Declare the exchange if it's not a built-in one
                string exchange = string.IsNullOrEmpty(settings.Exchange)
                    ? AmqpConstants.DefaultExchange
                    : settings.Exchange;
                if (!exchange.StartsWith("amq."))
                {
                    _channel.ExchangeDeclare(
                        exchange: exchange,
                        type: string.IsNullOrEmpty(settings.ExchangeType)
                            ? AmqpConstants.DefaultExchangeType
                            : settings.ExchangeType,
                        durable: true,
                        autoDelete: false);
                }

                // Declare an exclusive auto-delete queue for this consumer
                _queueName = _channel.QueueDeclare(
                    queue: "",
                    durable: false,
                    exclusive: true,
                    autoDelete: true).QueueName;

                // Set up consumer
                _consumer = new EventingBasicConsumer(_channel);
                _consumer.Received += OnMessageReceived;
                _channel.BasicConsume(queue: _queueName, autoAck: IsAutoAck(), consumer: _consumer);

                // QoS prefetch for AtLeastOnce / ExactlyOnce
                if (settings.Qos != AmqpConstants.QosAtMostOnce)
                {
                    _channel.BasicQos(prefetchSize: 0, prefetchCount: 100, global: false);
                }
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                try
                {
                    if (_channel != null && _channel.IsOpen)
                        _channel.Close();
                }
                catch { }

                try
                {
                    if (_connection != null && _connection.IsOpen)
                        _connection.Close();
                }
                catch { }

                _channel = null;
                _connection = null;
                _consumer = null;
                _subscriptions.Clear();
            }
        }

        public void DeclareEndpoint(string address)
        {
            lock (_lock)
            {
                if (_channel == null || !_channel.IsOpen)
                    return;

                string exchange = string.IsNullOrEmpty(_settings?.Exchange)
                    ? AmqpConstants.DefaultExchange
                    : _settings.Exchange;

                _channel.QueueBind(
                    queue: _queueName,
                    exchange: exchange,
                    routingKey: address);
            }
        }

        public void Subscribe(string address, ConcurrentQueue<AmqpMessage> receiveQueue)
        {
            lock (_lock)
            {
                _subscriptions[address] = receiveQueue;
                DeclareEndpoint(address);
            }
        }

        public void Unsubscribe(string address)
        {
            lock (_lock)
            {
                _subscriptions.Remove(address);

                if (_channel != null && _channel.IsOpen && _queueName != null)
                {
                    string exchange = string.IsNullOrEmpty(_settings?.Exchange)
                        ? AmqpConstants.DefaultExchange
                        : _settings.Exchange;

                    try
                    {
                        _channel.QueueUnbind(
                            queue: _queueName,
                            exchange: exchange,
                            routingKey: address);
                    }
                    catch { }
                }
            }
        }

        public void Publish(string address, byte[] payload, bool retained)
        {
            lock (_lock)
            {
                if (_channel == null || !_channel.IsOpen)
                    return;

                string exchange = string.IsNullOrEmpty(_settings?.Exchange)
                    ? AmqpConstants.DefaultExchange
                    : _settings.Exchange;

                var props = _channel.CreateBasicProperties();
                props.ContentType = "application/json";
                props.DeliveryMode = retained ? (byte)2 : (byte)1; // 2 = persistent
                props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                _channel.BasicPublish(
                    exchange: exchange,
                    routingKey: address,
                    mandatory: false,
                    basicProperties: props,
                    body: payload);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        private void OnMessageReceived(object sender, BasicDeliverEventArgs e)
        {
            var msg = new AmqpMessage
            {
                Address = e.RoutingKey,
                Payload = e.Body.ToArray(),
                Timestamp = DateTime.UtcNow
            };

            // Route to the matching subscription queue
            lock (_lock)
            {
                if (_subscriptions.TryGetValue(e.RoutingKey, out var queue))
                {
                    queue.Enqueue(msg);
                }
                else
                {
                    // Try wildcard match: check if any subscription key matches
                    foreach (var kvp in _subscriptions)
                    {
                        if (RoutingKeyMatches(kvp.Key, e.RoutingKey))
                        {
                            kvp.Value.Enqueue(msg);
                            break;
                        }
                    }
                }
            }

            // Acknowledge if not auto-ack
            if (!IsAutoAck() && _channel != null && _channel.IsOpen)
            {
                try { _channel.BasicAck(e.DeliveryTag, false); }
                catch { }
            }
        }

        private bool IsAutoAck()
        {
            return _settings?.Qos == AmqpConstants.QosAtMostOnce;
        }

        /// <summary>
        /// Simple AMQP topic routing key match (supports * and # wildcards).
        /// </summary>
        private static bool RoutingKeyMatches(string pattern, string routingKey)
        {
            if (pattern == routingKey) return true;
            if (pattern == "#") return true;

            string[] patternParts = pattern.Split('.');
            string[] keyParts = routingKey.Split('.');
            return MatchParts(patternParts, 0, keyParts, 0);
        }

        private static bool MatchParts(string[] pattern, int pi, string[] key, int ki)
        {
            if (pi == pattern.Length && ki == key.Length) return true;
            if (pi == pattern.Length) return false;

            if (pattern[pi] == "#")
            {
                // # matches zero or more words
                for (int i = ki; i <= key.Length; i++)
                {
                    if (MatchParts(pattern, pi + 1, key, i))
                        return true;
                }
                return false;
            }

            if (ki == key.Length) return false;

            if (pattern[pi] == "*" || pattern[pi] == key[ki])
                return MatchParts(pattern, pi + 1, key, ki + 1);

            return false;
        }
    }
}
