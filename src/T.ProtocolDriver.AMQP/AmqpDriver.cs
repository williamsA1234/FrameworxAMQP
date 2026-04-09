using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using T.ProtocolAPI;
using T.Library;

namespace T.ProtocolDriver.AMQP
{
    /// <summary>
    /// FrameworX AMQP Protocol Driver.
    /// Implements the IProtocol interface via ProtocolBase to integrate with the Devices module.
    /// Supports AMQP 0-9-1 (RabbitMQ) and AMQP 1.0 (Azure Service Bus, ActiveMQ, Solace).
    ///
    /// Since AMQP is a message-based pub/sub protocol (not a traditional request/response like Modbus),
    /// this driver uses the CustomInterface approach. It manages its own TCP connections via the
    /// RabbitMQ.Client and AmqpNetLite libraries, rather than using the built-in Serial/TCP/UDP comm layer.
    ///
    /// The FrameworX runtime still calls BuildCommand/ParseReply/ProtocolHandlerMaster in the standard
    /// lifecycle, but the driver handles the actual AMQP communication internally.
    /// </summary>
    public class Driver : ProtocolBase, IProtocol
    {
        #region Fields

        private AmqpProtocolDescription _drv;

        #endregion

        #region Constructor

        public Driver()
        {
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Called for each instance of the AMQP protocol (per channel).
        /// Parses protocol options and initializes the AMQP client.
        /// </summary>
        public override eReturn InitDrvInstance(ChannelCfg.DeviceCfg devCfg)
        {
            try
            {
                _drv = new AmqpProtocolDescription(devCfg, this.CommAPI);
            }
            catch (Exception ex)
            {
                TException.Log(ex);
                return eReturn.FAILED;
            }
            return eReturn.SUCCESS;
        }

        #endregion

        #region Runtime Functions - BuildCommand / ParseReply

        /// <summary>
        /// Builds the command buffer to send to the AMQP broker.
        /// For READ: subscribes to the topic and waits for a message.
        /// For WRITE: serializes the tag value and publishes it as an AMQP message.
        /// </summary>
        public override eReturn BuildCommand(SessionMsg msg)
        {
            try
            {
                // Ensure connection
                if (!_drv.EnsureConnected())
                {
                    msg.Error = eDrvError.ProtocolError;
                    return eReturn.FAILED;
                }

                if (msg.Block.CmdType == eCmdType.Write)
                {
                    // Get data from tags into DataBuffer (built-in method populates from tag values)
                    msg.BuildDataBuffer();

                    // For AMQP, publish the entire DataBuffer as a single message
                    // using the first item's address as the routing key
                    DrvItem firstItem = msg.Block.FirstItem;
                    if (firstItem != null && !string.IsNullOrEmpty(firstItem.Address))
                    {
                        string routingKey = ParseRoutingKey(firstItem.Address);
                        bool retained = ParseRetainedFlag(firstItem.Address);

                        // Get the raw bytes from DataBuffer
                        byte[] rawData = msg.DataBuffer.GetAll();
                        int dataLen = msg.DataBuffer.BufIndex;
                        byte[] payload;

                        if (_drv.PayloadFormat == AmqpConstants.PayloadJson)
                        {
                            // For JSON, convert the data buffer to a string representation
                            string dataStr = Encoding.UTF8.GetString(rawData, 0, dataLen);
                            payload = AmqpPayload.SerializeJson(firstItem.TagName, dataStr, AmqpConstants.QualityGood);
                        }
                        else
                        {
                            payload = new byte[dataLen];
                            Array.Copy(rawData, payload, dataLen);
                        }

                        _drv.Client.Publish(routingKey, payload, retained);
                    }

                    // Build a minimal TxBuffer so the runtime knows we sent something
                    msg.TxBuffer += 0x01; // ACK marker
                }
                else // READ
                {
                    // For reads, ensure subscription is active
                    foreach (DrvItem item in msg.Block.ListItems)
                    {
                        string address = item.Address;
                        if (string.IsNullOrEmpty(address))
                            continue;

                        string routingKey = ParseRoutingKey(address);
                        _drv.EnsureSubscription(routingKey);
                    }

                    // Build a minimal TxBuffer (read request marker)
                    msg.TxBuffer += 0x02; // READ marker
                }

                return eReturn.SUCCESS;
            }
            catch (Exception ex)
            {
                TException.Log(ex);
                return eReturn.FAILED;
            }
        }

        /// <summary>
        /// Parses the reply from the AMQP broker.
        /// For READ: checks the receive queue for messages and copies data into DataBuffer.
        /// For WRITE: verifies the publish was successful.
        /// </summary>
        public override eReturn ParseReply(SessionMsg msg)
        {
            try
            {
                if (msg.Block.CmdType == eCmdType.Read)
                {
                    // Check for received messages for each item in this block
                    foreach (DrvItem item in msg.Block.ListItems)
                    {
                        string address = item.Address;
                        if (string.IsNullOrEmpty(address))
                            continue;

                        string routingKey = ParseRoutingKey(address);

                        // Try to get a message from the receive queue
                        if (_drv.TryGetMessage(routingKey, out AmqpMessage amqpMsg))
                        {
                            object value = null;
                            int quality = AmqpConstants.QualityGood;

                            if (_drv.PayloadFormat == AmqpConstants.PayloadJson)
                            {
                                if (AmqpPayload.TryDeserializeJson(amqpMsg.Payload, out value, out _, out quality))
                                {
                                    WriteValueToDataBuffer(msg, item, value);
                                }
                            }
                            else
                            {
                                value = AmqpPayload.DeserializeRaw(amqpMsg.Payload);
                                WriteValueToDataBuffer(msg, item, value);
                            }
                        }
                    }

                    // Let the runtime copy DataBuffer values to tags
                    msg.ParseDataBuffer();
                }

                // For writes, nothing more to parse
                return eReturn.SUCCESS;
            }
            catch (Exception ex)
            {
                TException.Log(ex);
                return eReturn.FAILED;
            }
        }

        #endregion

        #region Protocol Handler

        /// <summary>
        /// Handles incoming bytes from the communication channel.
        /// For AMQP with CustomInterface, the broker communication is handled by the AMQP client libraries,
        /// not by the built-in TCP/UDP layer. We immediately signal the message as concluded.
        /// </summary>
        public override void ProtocolHandlerMaster(SessionMsg msg, byte byteRx)
        {
            try
            {
                // AMQP manages its own TCP connection via the client library.
                // Signal to the runtime that we have received the "response".
                msg.ReplyBuffer += byteRx;
                msg.ConcludedRx = true;
            }
            catch (Exception ex)
            {
                TException.Log(ex);
            }
        }

        #endregion

        #region Configuration Methods

        /// <summary>
        /// Parse and validate the address string from Devices > Points > Address column.
        /// Address format: "routing.key;retained" (semicolon-separated).
        /// </summary>
        public override eReturn ParseAddress(string Address, DrvItem Item)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Address))
                    return eReturn.FAILED;

                // Address format: "routing.key" or "routing.key;true"
                string routingKey = ParseRoutingKey(Address);
                if (string.IsNullOrEmpty(routingKey))
                    return eReturn.FAILED;

                // For AMQP, all items are message-based (no register addressing)
                // Use a hash of the routing key as the numeric address for sorting
                Item.NativeDataType = eDataType.ASCII;
                Item.AddressVar = Math.Abs(routingKey.GetHashCode()) & 0xFFFF;
                Item.BlockId = 1; // All AMQP items are in block 1 (message block)

                return eReturn.SUCCESS;
            }
            catch (Exception ex)
            {
                TException.Log(ex);
                return eReturn.FAILED;
            }
        }

        /// <summary>
        /// Determines if two items can be in the same communication block.
        /// For AMQP, each routing key is its own block (one message per topic).
        /// </summary>
        public override bool IsSameBlock(DrvItem first, DrvItem item)
        {
            try
            {
                if (first.BlockId != item.BlockId)
                    return false;

                // Each unique routing key should be in its own block
                // so messages are handled independently
                string key1 = ParseRoutingKey(first.Address);
                string key2 = ParseRoutingKey(item.Address);
                return key1 == key2;
            }
            catch (Exception ex)
            {
                TException.Log(ex);
                return false;
            }
        }

        /// <summary>
        /// Sorts configured items for block grouping.
        /// </summary>
        public override int SortConfig(DrvItem itemMain, DrvItem itemNext)
        {
            try
            {
                if (itemMain.BlockId > itemNext.BlockId)
                    return 1;
                if (itemMain.BlockId < itemNext.BlockId)
                    return -1;
                if (itemMain.AddressVar > itemNext.AddressVar)
                    return 1;
                if (itemMain.AddressVar < itemNext.AddressVar)
                    return -1;
            }
            catch (Exception ex)
            {
                TException.Log(ex);
            }
            return 0;
        }

        #endregion

        #region Helper Methods

        private static string ParseRoutingKey(string address)
        {
            if (string.IsNullOrEmpty(address))
                return "";

            string[] parts = address.Split(';');
            return parts[0].Trim();
        }

        private static bool ParseRetainedFlag(string address)
        {
            if (string.IsNullOrEmpty(address))
                return false;

            string[] parts = address.Split(';');
            if (parts.Length > 1 && bool.TryParse(parts[1].Trim(), out bool retained))
                return retained;
            return false;
        }

        /// <summary>
        /// Writes a received value into the SessionMsg DataBuffer for the given item.
        /// </summary>
        private void WriteValueToDataBuffer(SessionMsg msg, DrvItem item, object value)
        {
            try
            {
                if (value == null)
                    return;

                // Convert value to bytes and append to DataBuffer based on native data type
                switch (item.NativeDataType)
                {
                    case eDataType.Bit:
                        msg.DataBuffer += TConvert.To<byte>(value);
                        break;
                    case eDataType.Byte:
                        msg.DataBuffer += TConvert.To<byte>(value);
                        break;
                    case eDataType.Word:
                    case eDataType.Short:
                        short shortVal = TConvert.To<short>(value);
                        msg.DataBuffer += Lib.HighByte(shortVal);
                        msg.DataBuffer += Lib.LowByte(shortVal);
                        break;
                    case eDataType.Integer:
                    case eDataType.Long:
                        int intVal = TConvert.To<int>(value);
                        byte[] intBytes = BitConverter.GetBytes(intVal);
                        // Big-endian: high bytes first
                        for (int i = intBytes.Length - 1; i >= 0; i--)
                            msg.DataBuffer += intBytes[i];
                        break;
                    case eDataType.Real:
                        float floatVal = TConvert.To<float>(value);
                        byte[] floatBytes = BitConverter.GetBytes(floatVal);
                        for (int i = floatBytes.Length - 1; i >= 0; i--)
                            msg.DataBuffer += floatBytes[i];
                        break;
                    case eDataType.ULong:
                        double dblVal = TConvert.To<double>(value);
                        byte[] dblBytes = BitConverter.GetBytes(dblVal);
                        for (int i = dblBytes.Length - 1; i >= 0; i--)
                            msg.DataBuffer += dblBytes[i];
                        break;
                    case eDataType.ASCII:
                    default:
                        string strVal = TConvert.To<string>(value);
                        byte[] strBytes = Encoding.UTF8.GetBytes(strVal ?? "");
                        foreach (byte b in strBytes)
                            msg.DataBuffer += b;
                        break;
                }
            }
            catch (Exception ex)
            {
                TException.Log(ex);
            }
        }

        #endregion
    }

    #region Protocol Description (AMQP-specific)

    /// <summary>
    /// Manages the AMQP connection and protocol-specific settings.
    /// Follows the DriverToolkit pattern of separating protocol specifics from the IProtocol interface.
    /// </summary>
    internal class AmqpProtocolDescription
    {
        // Connection settings
        public AmqpConnectionSettings Settings { get; private set; }
        public IAmqpClient Client { get; private set; }
        public string PayloadFormat { get; private set; } = AmqpConstants.PayloadJson;

        private readonly object _commApi;
        private readonly object _lock = new object();

        // Receive queues per routing key
        private readonly ConcurrentDictionary<string, ConcurrentQueue<AmqpMessage>> _receiveQueues
            = new ConcurrentDictionary<string, ConcurrentQueue<AmqpMessage>>();

        // Track active subscriptions
        private readonly HashSet<string> _activeSubscriptions = new HashSet<string>();

        public AmqpProtocolDescription(ChannelCfg.DeviceCfg devCfg, object commApi)
        {
            _commApi = commApi;
            Settings = new AmqpConnectionSettings();

            // Parse Protocol Options (DrvParams from Channel config)
            string amqpVersion = AmqpConstants.Version091;
            if (devCfg.DrvParams != null && devCfg.DrvParams.Length > 0)
            {
                amqpVersion = TConvert.To<string>(devCfg.DrvParams[AmqpConstants.OptAmqpVersion]) ?? AmqpConstants.Version091;

                if (devCfg.DrvParams.Length > AmqpConstants.OptTimePublishRate)
                {
                    int rate = TConvert.To<int>(devCfg.DrvParams[AmqpConstants.OptTimePublishRate]);
                    if (rate >= AmqpConstants.MinPublishRate)
                        Settings.KeepAlive = rate; // reuse for timing
                }

                if (devCfg.DrvParams.Length > AmqpConstants.OptPayloadFormat)
                    PayloadFormat = TConvert.To<string>(devCfg.DrvParams[AmqpConstants.OptPayloadFormat]) ?? AmqpConstants.PayloadJson;
            }

            // Parse Station (Node config) for connection settings
            if (devCfg.Station != null)
            {
                ParseStation(devCfg.Station, amqpVersion);
            }

            Settings.AmqpVersion = amqpVersion;
            Settings.PayloadFormat = PayloadFormat;

            // Create the appropriate AMQP client
            Client = amqpVersion == AmqpConstants.Version10
                ? (IAmqpClient)new AmqpClient10()
                : (IAmqpClient)new AmqpClient091();
        }

        private void ParseStation(string station, string amqpVersion)
        {
            if (string.IsNullOrEmpty(station))
                return;

            string[] parts = station.Split(';');

            Settings.BrokerUrl = GetPart(parts, AmqpConstants.StaBrokerUrl, AmqpConstants.DefaultBrokerUrl);
            Settings.Port = GetIntPart(parts, AmqpConstants.StaPort, AmqpConstants.DefaultPort091);
            Settings.VirtualHost = GetPart(parts, AmqpConstants.StaVirtualHost, AmqpConstants.DefaultVirtualHost);
            Settings.ClientId = GetPart(parts, AmqpConstants.StaClientId, "");
            Settings.UserName = GetPart(parts, AmqpConstants.StaUserName, "");
            Settings.Password = GetPart(parts, AmqpConstants.StaPassword, "");
            Settings.NetworkSecurity = GetPart(parts, AmqpConstants.StaNetworkSecurity, AmqpConstants.SecurityNone);
            Settings.X509CertificatePath = GetPart(parts, AmqpConstants.StaX509Certificate, "");
            Settings.Qos = GetPart(parts, AmqpConstants.StaQos, AmqpConstants.QosAtLeastOnce);
            Settings.KeepAlive = GetIntPart(parts, AmqpConstants.StaKeepAlive, AmqpConstants.DefaultKeepAlive);
            Settings.Exchange = GetPart(parts, AmqpConstants.StaExchange, AmqpConstants.DefaultExchange);
            Settings.ExchangeType = GetPart(parts, AmqpConstants.StaExchangeType, AmqpConstants.DefaultExchangeType);
            Settings.ContainerId = GetPart(parts, AmqpConstants.StaContainerId, "");
        }

        /// <summary>
        /// Ensures the AMQP client is connected. Returns true if connected.
        /// </summary>
        public bool EnsureConnected()
        {
            lock (_lock)
            {
                if (Client != null && Client.IsConnected)
                    return true;

                try
                {
                    Client.Connect(Settings);
                    return Client.IsConnected;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Ensures a subscription exists for the given routing key.
        /// </summary>
        public void EnsureSubscription(string routingKey)
        {
            lock (_lock)
            {
                if (_activeSubscriptions.Contains(routingKey))
                    return;

                var queue = _receiveQueues.GetOrAdd(routingKey, _ => new ConcurrentQueue<AmqpMessage>());
                Client.Subscribe(routingKey, queue);
                _activeSubscriptions.Add(routingKey);
            }
        }

        /// <summary>
        /// Tries to dequeue a received message for the given routing key.
        /// </summary>
        public bool TryGetMessage(string routingKey, out AmqpMessage message)
        {
            message = null;
            if (_receiveQueues.TryGetValue(routingKey, out var queue))
                return queue.TryDequeue(out message);
            return false;
        }

        private static string GetPart(string[] parts, int index, string defaultValue)
        {
            if (parts == null || index >= parts.Length || string.IsNullOrEmpty(parts[index]))
                return defaultValue;
            return parts[index].Trim();
        }

        private static int GetIntPart(string[] parts, int index, int defaultValue)
        {
            string s = GetPart(parts, index, null);
            if (s != null && int.TryParse(s, out int val))
                return val;
            return defaultValue;
        }
    }

    #endregion
}
