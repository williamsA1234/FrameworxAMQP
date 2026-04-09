# FrameworX AMQP Connector

Custom protocol driver for FrameworX that provides AMQP (Advanced Message Queuing Protocol) connectivity to industrial data systems.

## Features

- **AMQP 0-9-1** support via RabbitMQ.Client (RabbitMQ, LavinMQ, etc.)
- **AMQP 1.0** support via AmqpNetLite (Azure Service Bus, ActiveMQ, Solace, Qpid, etc.)
- Publish and Subscribe (bidirectional data exchange)
- JSON payload format (name/value/timestamp/quality) matching FrameworX MQTT conventions
- Raw payload format for simple value exchange
- TLS 1.2 / 1.3 with X.509 certificate authentication
- QoS levels: AtMostOnce, AtLeastOnce, ExactlyOnce
- Store-and-forward for reliable delivery
- Automatic reconnection
- Multiplatform (.NET Standard 2.0)

## Prerequisites

- [.NET SDK 6.0+](https://dotnet.microsoft.com/download) (for building) or Visual Studio 2022
- FrameworX 10.x installed
- `T.ProtocolAPI.dll` and `T.Library.dll` from FrameworX installation (`fx-10\` folder)

## Project Structure

```
FrameworxAMQP/
├── src/T.ProtocolDriver.AMQP/     # Driver source code
│   ├── AmqpDriver.cs              # Main driver class (extends ProtocolBase, implements IProtocol)
│   ├── AmqpClient091.cs           # AMQP 0-9-1 (RabbitMQ) client
│   ├── AmqpClient10.cs            # AMQP 1.0 client
│   ├── IAmqpClient.cs             # Common interface
│   ├── AmqpPayload.cs             # JSON payload serialization
│   ├── AmqpConstants.cs           # Constants and defaults
│   └── Properties/AssemblyInfo.cs # Assembly metadata
├── config/
│   └── T.ProtocolDriver.AMQP.config.xml  # FrameworX protocol descriptor
├── lib/
│   ├── T.ProtocolAPI.dll          # FrameworX Protocol API (reference)
│   └── T.Library.dll              # FrameworX utility library (reference)
└── README.md
```

## Build

```bash
dotnet restore
dotnet build -c Release
```

Output: `src/T.ProtocolDriver.AMQP/bin/Release/T.ProtocolDriver.AMQP.dll`

## Deploy

1. Copy the built DLL and its dependencies to the FrameworX Protocols folder:

```powershell
$protocols = "C:\Program Files\Tatsoft\FrameworX\fx-10\Protocols"
$output = "src\T.ProtocolDriver.AMQP\bin\Release"

# Copy driver assembly
Copy-Item "$output\T.ProtocolDriver.AMQP.dll" $protocols
Copy-Item "$output\T.ProtocolDriver.AMQP.deps.json" $protocols

# Copy AMQP client dependencies
Copy-Item "$output\RabbitMQ.Client.dll" $protocols
Copy-Item "$output\AMQPNetLite.Core.dll" $protocols
Copy-Item "$output\Newtonsoft.Json.dll" $protocols
Copy-Item "$output\System.Threading.Channels.dll" $protocols -ErrorAction SilentlyContinue

# Copy config XML
Copy-Item "config\T.ProtocolDriver.AMQP.config.xml" $protocols

# Also deploy to net8.0 Protocols for multiplatform
$protocols8 = "C:\Program Files\Tatsoft\FrameworX\fx-10\net8.0\Protocols"
Copy-Item "$output\T.ProtocolDriver.AMQP.dll" $protocols8
```

2. Restart FrameworX Designer.

3. The **AMQP** protocol should now appear in Devices > Channels > Protocol dropdown.

## Configuration

### Channel (Protocol Options)

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| AmqpVersion | ComboBox | 0-9-1 | Protocol version: `0-9-1` (RabbitMQ) or `1.0` |
| TimePublishRate | int | 500 | Publish interval in milliseconds (min 50ms) |
| StoreAndForward | bool | false | Queue messages when broker is unreachable |
| PayloadFormat | ComboBox | JSON | `JSON` or `Raw` payload format |

### Node (Station)

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| BrokerURL | string | localhost | AMQP broker hostname or IP |
| Port | int | 5672 | Broker port (5672=standard, 5671=TLS) |
| VirtualHost | string | / | Virtual host (AMQP 0-9-1 only) |
| ClientID | string | auto | Unique client identifier |
| UserName | string | | Authentication username |
| Password | string | | Authentication password |
| NetworkSecurity | ComboBox | None | TLS: `None`, `TLSv1.2`, `TLSv1.3` |
| X509Certificate | string | | Path to X.509 client certificate |
| QoS | ComboBox | AtLeastOnce | Quality of Service level |
| KeepAlive | int | 60 | Heartbeat interval in seconds |
| Exchange | string | amq.topic | Exchange name (AMQP 0-9-1) |
| ExchangeType | ComboBox | topic | Exchange type: `topic`, `direct`, `fanout`, `headers` |
| ContainerID | string | | Container ID (AMQP 1.0) |

### Point (Address)

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| Address | string | | Routing key (0-9-1) or AMQP address (1.0) |
| RetainedMessage | bool | false | Request durable message delivery |

### Station Examples

**Local RabbitMQ (no auth):**
```
localhost;5672;/;MyClient;;;None;;AtLeastOnce;60;amq.topic;topic;
```

**RabbitMQ with credentials:**
```
localhost;5672;/;MyClient;guest;guest;None;;AtLeastOnce;60;amq.topic;topic;
```

**RabbitMQ with TLS:**
```
rabbitmq.example.com;5671;/;MyClient;user;pass;TLSv1.2;C:\certs\client.pfx;AtLeastOnce;60;amq.topic;topic;
```

**AMQP 1.0 broker:**
```
amqp-broker.example.com;5672;/;MyClient;user;pass;None;;AtLeastOnce;60;;;MyContainer
```

### Address Examples

```
plant.line1.temperature        # Simple routing key
plant.line1.#                  # Wildcard (all under line1)
sensor.*.pressure              # Single-word wildcard
```

## JSON Payload Format

Messages are serialized as JSON matching the FrameworX MQTT flat convention:

```json
{
  "name": "Tag.Plant1.Temperature",
  "value": 72.5,
  "timestamp": 1712160000000,
  "quality": 192
}
```

| Field | Description |
|-------|-------------|
| name | Tag name from the solution |
| value | Current tag value |
| timestamp | Unix timestamp in milliseconds (UTC) |
| quality | FrameworX quality code (192 = Good) |

## DriverToolkit Architecture

This connector is built using the Tatsoft DriverToolkit (`T.ProtocolAPI.dll` + `T.Library.dll`).

The main `Driver` class in `AmqpDriver.cs` follows the standard DriverToolkit pattern:

```csharp
public class Driver : ProtocolBase, IProtocol
```

**Required override methods:**

| Method | Purpose |
|--------|---------|
| `InitDrvInstance(ChannelCfg.DeviceCfg)` | Parse protocol options, create AMQP client |
| `BuildCommand(SessionMsg)` | For writes: publish to AMQP. For reads: ensure subscriptions |
| `ParseReply(SessionMsg)` | For reads: dequeue messages, populate DataBuffer |
| `ProtocolHandlerMaster(SessionMsg, byte)` | Handle incoming bytes (immediately concludes for AMQP) |
| `ParseAddress(string, DrvItem)` | Validate and parse routing key addresses |
| `IsSameBlock(DrvItem, DrvItem)` | Group items by routing key |
| `SortConfig(DrvItem, DrvItem)` | Sort items for block creation |

**Key DriverToolkit types used:**
- `SessionMsg` - Contains TxBuffer, RxBuffer, DataBuffer, Block info
- `DrvItem` - Individual point with Address, NativeDataType, BlockId, AddressVar
- `ChannelCfg.DeviceCfg` - Channel config with DrvParams, Station, MaxProtocolBufferSize
- `GenericBuf` - Buffer class with `+=` operator for appending bytes
- `TConvert.To<T>()` - Safe type conversion
- `TException.Log()` - Exception logging to FrameworX trace
- `Lib` - Utility methods (HighByte, LowByte, etc.)

## Version History

| Version | Notes |
|---------|-------|
| 1.0.0.0 | Initial release. AMQP 0-9-1 and 1.0 support. |
