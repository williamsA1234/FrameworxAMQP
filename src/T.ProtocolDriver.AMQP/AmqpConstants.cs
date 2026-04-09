namespace T.ProtocolDriver.AMQP
{
    /// <summary>
    /// Constants and default values for the AMQP protocol driver.
    /// </summary>
    internal static class AmqpConstants
    {
        public const string ProtocolName = "AMQP";
        public const string DriverVersion = "1.0.0.0";

        // AMQP Versions
        public const string Version091 = "0-9-1";
        public const string Version10 = "1.0";

        // Default connection settings
        public const string DefaultBrokerUrl = "localhost";
        public const int DefaultPort091 = 5672;
        public const int DefaultPortTls = 5671;
        public const string DefaultVirtualHost = "/";
        public const int DefaultKeepAlive = 60;
        public const int DefaultPublishRate = 500;
        public const int MinPublishRate = 50;

        // Default exchange settings (AMQP 0-9-1)
        public const string DefaultExchange = "amq.topic";
        public const string DefaultExchangeType = "topic";

        // QoS levels
        public const string QosAtMostOnce = "AtMostOnce";
        public const string QosAtLeastOnce = "AtLeastOnce";
        public const string QosExactlyOnce = "ExactlyOnce";

        // TLS options
        public const string SecurityNone = "None";
        public const string SecurityTls12 = "TLSv1.2";
        public const string SecurityTls13 = "TLSv1.3";

        // Payload formats
        public const string PayloadJson = "JSON";
        public const string PayloadRaw = "Raw";

        // Protocol Options indices (channel-level DrvParams)
        public const int OptAmqpVersion = 0;
        public const int OptTimePublishRate = 1;
        public const int OptStoreAndForward = 2;
        public const int OptPayloadFormat = 3;

        // Station parameter indices (node-level)
        public const int StaBrokerUrl = 0;
        public const int StaPort = 1;
        public const int StaVirtualHost = 2;
        public const int StaClientId = 3;
        public const int StaUserName = 4;
        public const int StaPassword = 5;
        public const int StaNetworkSecurity = 6;
        public const int StaX509Certificate = 7;
        public const int StaQos = 8;
        public const int StaKeepAlive = 9;
        public const int StaExchange = 10;
        public const int StaExchangeType = 11;
        public const int StaContainerId = 12;

        // Address parameter indices (point-level)
        public const int AddrAddress = 0;
        public const int AddrRetained = 1;

        // Quality codes (FrameworX standard)
        public const int QualityGood = 192;
        public const int QualityBad = 0;
        public const int QualityUncertain = 64;

        // Reconnection
        public const int ReconnectDelayMs = 5000;
        public const int MaxReconnectDelayMs = 60000;
    }
}
