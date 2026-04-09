using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Amqp;
using Amqp.Framing;

namespace T.ProtocolDriver.AMQP
{
    /// <summary>
    /// AMQP 1.0 client implementation using AmqpNetLite.
    /// Manages connection, session, sender/receiver links.
    /// </summary>
    internal class AmqpClient10 : IAmqpClient
    {
        private Connection _connection;
        private Session _session;
        private readonly object _lock = new object();
        private AmqpConnectionSettings _settings;
        private bool _disposed;

        private readonly Dictionary<string, ReceiverLink> _receivers
            = new Dictionary<string, ReceiverLink>();
        private readonly Dictionary<string, SenderLink> _senders
            = new Dictionary<string, SenderLink>();
        private readonly Dictionary<string, ConcurrentQueue<AmqpMessage>> _subscriptions
            = new Dictionary<string, ConcurrentQueue<AmqpMessage>>();

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return _connection != null && !_connection.IsClosed
                        && _session != null && !_session.IsClosed;
                }
            }
        }

        public void Connect(AmqpConnectionSettings settings)
        {
            lock (_lock)
            {
                _settings = settings;

                string scheme = settings.NetworkSecurity != AmqpConstants.SecurityNone
                    && !string.IsNullOrEmpty(settings.NetworkSecurity)
                    ? "amqps" : "amqp";
                int port = settings.Port > 0 ? settings.Port : AmqpConstants.DefaultPort091;

                var address = new Address(settings.BrokerUrl, port, settings.UserName, settings.Password, "/", scheme);

                var factory = new ConnectionFactory();

                // TLS configuration
                if (scheme == "amqps")
                {
                    factory.SSL.RemoteCertificateValidationCallback = ValidateServerCertificate;

                    if (!string.IsNullOrEmpty(settings.X509CertificatePath))
                    {
                        var cert = new X509Certificate2(settings.X509CertificatePath);
                        factory.SSL.ClientCertificates.Add(cert);
                    }

                    switch (settings.NetworkSecurity)
                    {
                        case AmqpConstants.SecurityTls12:
                            factory.SSL.Protocols = SslProtocols.Tls12;
                            break;
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER
                        case AmqpConstants.SecurityTls13:
                            factory.SSL.Protocols = SslProtocols.Tls13;
                            break;
#endif
                        default:
                            factory.SSL.Protocols = SslProtocols.Tls12;
                            break;
                    }
                }

                // Container ID
                string containerId = string.IsNullOrEmpty(settings.ContainerId)
                    ? settings.ClientId
                    : settings.ContainerId;
                if (string.IsNullOrEmpty(containerId))
                    containerId = Guid.NewGuid().ToString();

                var open = new Open
                {
                    ContainerId = containerId,
                    IdleTimeOut = (uint)(settings.KeepAlive > 0 ? settings.KeepAlive * 1000 : AmqpConstants.DefaultKeepAlive * 1000)
                };

                _connection = factory.CreateAsync(address, open, null).GetAwaiter().GetResult();
                _session = new Session(_connection);
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                foreach (var kvp in _receivers)
                {
                    try { kvp.Value.Close(); } catch { }
                }
                _receivers.Clear();

                foreach (var kvp in _senders)
                {
                    try { kvp.Value.Close(); } catch { }
                }
                _senders.Clear();
                _subscriptions.Clear();

                try { _session?.Close(); } catch { }
                try { _connection?.Close(); } catch { }

                _session = null;
                _connection = null;
            }
        }

        public void DeclareEndpoint(string address)
        {
            // In AMQP 1.0, endpoints are created via links (see Subscribe/Publish)
        }

        public void Subscribe(string address, ConcurrentQueue<AmqpMessage> receiveQueue)
        {
            lock (_lock)
            {
                if (_session == null || _session.IsClosed)
                    return;

                _subscriptions[address] = receiveQueue;

                if (!_receivers.ContainsKey(address))
                {
                    string linkName = $"receiver-{address}-{Guid.NewGuid():N}";
                    var source = new Source { Address = address };

                    var receiver = new ReceiverLink(_session, linkName, source, null);
                    receiver.Start(10, (link, message) => OnMessageReceived(address, link, message));
                    _receivers[address] = receiver;
                }
            }
        }

        public void Unsubscribe(string address)
        {
            lock (_lock)
            {
                _subscriptions.Remove(address);

                if (_receivers.TryGetValue(address, out var receiver))
                {
                    try { receiver.Close(); } catch { }
                    _receivers.Remove(address);
                }
            }
        }

        public void Publish(string address, byte[] payload, bool retained)
        {
            lock (_lock)
            {
                if (_session == null || _session.IsClosed)
                    return;

                if (!_senders.TryGetValue(address, out var sender) || sender.IsClosed)
                {
                    string linkName = $"sender-{address}-{Guid.NewGuid():N}";
                    var target = new Target { Address = address };
                    sender = new SenderLink(_session, linkName, target, null);
                    _senders[address] = sender;
                }

                var msg = new Amqp.Message(payload)
                {
                    Properties = new Properties
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        ContentType = "application/json",
                        CreationTime = DateTime.UtcNow
                    },
                    Header = new Header
                    {
                        Durable = retained
                    }
                };

                // Set settlement mode based on QoS
                if (_settings?.Qos == AmqpConstants.QosAtMostOnce)
                    sender.Send(msg); // pre-settled (fire and forget)
                else
                    sender.Send(msg, null, null); // wait for disposition
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        private void OnMessageReceived(string subscriptionAddress, IReceiverLink link, Amqp.Message message)
        {
            try
            {
                byte[] payload;
                if (message.Body is byte[] bytes)
                    payload = bytes;
                else if (message.Body != null)
                    payload = System.Text.Encoding.UTF8.GetBytes(message.Body.ToString());
                else
                    payload = new byte[0];

                var amqpMsg = new AmqpMessage
                {
                    Address = subscriptionAddress,
                    Payload = payload,
                    Timestamp = message.Properties?.CreationTime ?? DateTime.UtcNow
                };

                lock (_lock)
                {
                    if (_subscriptions.TryGetValue(subscriptionAddress, out var queue))
                    {
                        queue.Enqueue(amqpMsg);
                    }
                }

                // Accept the message (acknowledge)
                if (_settings?.Qos != AmqpConstants.QosAtMostOnce)
                    link.Accept(message);
            }
            catch
            {
                try { link.Reject(message); } catch { }
            }
        }

        private static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // Accept all certificates in industrial environments
            // Production deployments should implement proper certificate validation
            return true;
        }
    }
}
