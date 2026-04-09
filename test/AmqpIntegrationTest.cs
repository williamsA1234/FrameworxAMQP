using System;
using System.Text;
using System.Threading;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Newtonsoft.Json.Linq;

/// <summary>
/// Standalone integration test for the FrameworX AMQP connector.
/// Tests: RabbitMQ connectivity, publish, subscribe, and JSON payload round-trip.
/// </summary>
class AmqpIntegrationTest
{
    const string BrokerHost = "localhost";
    const int BrokerPort = 5673;
    const string User = "guest";
    const string Pass = "guest";
    const string Exchange = "amq.topic";
    const string RoutingKey = "plant.line1.temperature";

    static int Main(string[] args)
    {
        int passed = 0;
        int failed = 0;

        Console.WriteLine("=== FrameworX AMQP Connector - Integration Test ===");
        Console.WriteLine($"Broker: {BrokerHost}:{BrokerPort}");
        Console.WriteLine();

        // Test 1: Connection
        Console.Write("[TEST 1] Connect to RabbitMQ broker... ");
        IConnection connection = null;
        IModel channel = null;
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = BrokerHost,
                Port = BrokerPort,
                UserName = User,
                Password = Pass,
                VirtualHost = "/",
                RequestedHeartbeat = TimeSpan.FromSeconds(60)
            };
            connection = factory.CreateConnection();
            channel = connection.CreateModel();
            Console.WriteLine("PASS");
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL - {ex.Message}");
            failed++;
            return 1;
        }

        // Test 2: Declare queue and bind to exchange
        string queueName = "";
        Console.Write("[TEST 2] Declare queue and bind to amq.topic... ");
        try
        {
            queueName = channel.QueueDeclare("", false, true, true).QueueName;
            channel.QueueBind(queueName, Exchange, RoutingKey);
            Console.WriteLine($"PASS (queue: {queueName.Substring(0, 8)}...)");
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL - {ex.Message}");
            failed++;
        }

        // Test 3: Publish JSON payload (simulates FrameworX WRITE)
        Console.Write("[TEST 3] Publish JSON message to plant.line1.temperature... ");
        try
        {
            var payload = new JObject
            {
                ["name"] = "AMQP_Test/Temperature",
                ["value"] = 72.5,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["quality"] = 192
            };
            byte[] body = Encoding.UTF8.GetBytes(payload.ToString());

            var props = channel.CreateBasicProperties();
            props.ContentType = "application/json";
            props.DeliveryMode = 1;

            channel.BasicPublish(Exchange, RoutingKey, false, props, body);
            Console.WriteLine("PASS");
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL - {ex.Message}");
            failed++;
        }

        // Test 4: Consume and verify the message (simulates FrameworX READ)
        Console.Write("[TEST 4] Receive and parse JSON message... ");
        try
        {
            var received = new ManualResetEventSlim(false);
            string receivedBody = null;

            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += (sender, e) =>
            {
                receivedBody = Encoding.UTF8.GetString(e.Body.ToArray());
                received.Set();
            };
            channel.BasicConsume(queueName, true, consumer);

            if (received.Wait(TimeSpan.FromSeconds(5)))
            {
                var obj = JObject.Parse(receivedBody);
                string name = obj["name"]?.ToString();
                double value = obj["value"]?.Value<double>() ?? 0;
                int quality = obj["quality"]?.Value<int>() ?? 0;

                if (name == "AMQP_Test/Temperature" && Math.Abs(value - 72.5) < 0.01 && quality == 192)
                {
                    Console.WriteLine($"PASS (name={name}, value={value}, quality={quality})");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAIL - unexpected values: name={name}, value={value}, quality={quality}");
                    failed++;
                }
            }
            else
            {
                Console.WriteLine("FAIL - timeout waiting for message");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL - {ex.Message}");
            failed++;
        }

        // Test 5: Multiple routing keys (simulates multiple Points)
        Console.Write("[TEST 5] Multi-topic pub/sub... ");
        try
        {
            string queue2 = channel.QueueDeclare("", false, true, true).QueueName;
            channel.QueueBind(queue2, Exchange, "plant.line1.pressure");
            channel.QueueBind(queue2, Exchange, "plant.line1.flow");

            // Publish to both topics
            var pressurePayload = Encoding.UTF8.GetBytes(
                new JObject { ["name"] = "Pressure", ["value"] = 14.7, ["timestamp"] = 0, ["quality"] = 192 }.ToString());
            var flowPayload = Encoding.UTF8.GetBytes(
                new JObject { ["name"] = "Flow", ["value"] = 250.0, ["timestamp"] = 0, ["quality"] = 192 }.ToString());

            channel.BasicPublish(Exchange, "plant.line1.pressure", false, null, pressurePayload);
            channel.BasicPublish(Exchange, "plant.line1.flow", false, null, flowPayload);

            int msgCount = 0;
            var consumer2 = new EventingBasicConsumer(channel);
            var allReceived = new ManualResetEventSlim(false);
            consumer2.Received += (s, e) => { if (++msgCount >= 2) allReceived.Set(); };
            channel.BasicConsume(queue2, true, consumer2);

            if (allReceived.Wait(TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine($"PASS ({msgCount} messages received)");
                passed++;
            }
            else
            {
                Console.WriteLine($"FAIL - only received {msgCount}/2 messages");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL - {ex.Message}");
            failed++;
        }

        // Test 6: Verify driver DLL and config deployed to FrameworX
        Console.Write("[TEST 6] Verify T.ProtocolDriver.AMQP deployed to FrameworX... ");
        try
        {
            string[] requiredFiles = {
                @"C:\Program Files\Tatsoft\FrameworX\fx-10\Protocols\T.ProtocolDriver.AMQP.dll",
                @"C:\Program Files\Tatsoft\FrameworX\fx-10\Protocols\T.ProtocolDriver.AMQP.config.xml",
                @"C:\Program Files\Tatsoft\FrameworX\fx-10\Protocols\T.ProtocolDriver.AMQP.deps.json",
                @"C:\Program Files\Tatsoft\FrameworX\fx-10\Protocols\RabbitMQ.Client.dll",
                @"C:\Program Files\Tatsoft\FrameworX\fx-10\Protocols\Amqp.Net.dll",
            };
            int found = 0;
            string missing = "";
            foreach (var f in requiredFiles)
            {
                if (System.IO.File.Exists(f))
                    found++;
                else
                    missing += System.IO.Path.GetFileName(f) + " ";
            }
            if (found == requiredFiles.Length)
            {
                var fi = new System.IO.FileInfo(requiredFiles[0]);
                Console.WriteLine($"PASS ({found}/{requiredFiles.Length} files, DLL={fi.Length / 1024}KB)");
                passed++;
            }
            else
            {
                Console.WriteLine($"FAIL - missing: {missing.Trim()} ({found}/{requiredFiles.Length})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL - {ex.Message}");
            failed++;
        }

        // Cleanup
        try { channel?.Close(); } catch { }
        try { connection?.Close(); } catch { }

        // Summary
        Console.WriteLine();
        Console.WriteLine($"=== Results: {passed} passed, {failed} failed, {passed + failed} total ===");
        return failed > 0 ? 1 : 0;
    }
}
