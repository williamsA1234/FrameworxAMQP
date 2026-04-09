using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace T.ProtocolDriver.AMQP
{
    /// <summary>
    /// Handles JSON payload serialization/deserialization for AMQP messages.
    /// JSON format matches the FrameworX MQTT flat payload convention.
    /// </summary>
    internal static class AmqpPayload
    {
        /// <summary>
        /// Serialize a tag value to a JSON payload.
        /// </summary>
        public static byte[] SerializeJson(string tagName, object value, int quality)
        {
            var payload = new JObject
            {
                ["name"] = tagName,
                ["value"] = value != null ? JToken.FromObject(value) : JValue.CreateNull(),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["quality"] = quality
            };
            string json = payload.ToString(Formatting.None);
            return Encoding.UTF8.GetBytes(json);
        }

        /// <summary>
        /// Serialize a raw value to bytes.
        /// </summary>
        public static byte[] SerializeRaw(object value)
        {
            if (value == null)
                return new byte[0];

            if (value is byte[] bytes)
                return bytes;

            return Encoding.UTF8.GetBytes(Convert.ToString(value));
        }

        /// <summary>
        /// Deserialize a JSON payload to extract value, timestamp, and quality.
        /// </summary>
        public static bool TryDeserializeJson(byte[] data, out object value, out long timestamp, out int quality)
        {
            value = null;
            timestamp = 0;
            quality = AmqpConstants.QualityGood;

            try
            {
                string json = Encoding.UTF8.GetString(data);
                var obj = JObject.Parse(json);

                JToken valueToken = obj["value"];
                if (valueToken != null)
                {
                    switch (valueToken.Type)
                    {
                        case JTokenType.Integer:
                            value = valueToken.Value<long>();
                            break;
                        case JTokenType.Float:
                            value = valueToken.Value<double>();
                            break;
                        case JTokenType.Boolean:
                            value = valueToken.Value<bool>();
                            break;
                        case JTokenType.String:
                            value = valueToken.Value<string>();
                            break;
                        case JTokenType.Null:
                            value = null;
                            break;
                        default:
                            value = valueToken.ToString();
                            break;
                    }
                }

                JToken tsToken = obj["timestamp"];
                if (tsToken != null)
                    timestamp = tsToken.Value<long>();

                JToken qToken = obj["quality"];
                if (qToken != null)
                    quality = qToken.Value<int>();

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Deserialize raw payload as a string value.
        /// </summary>
        public static object DeserializeRaw(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            string text = Encoding.UTF8.GetString(data);

            if (int.TryParse(text, out int intVal))
                return intVal;
            if (long.TryParse(text, out long longVal))
                return longVal;
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double dblVal))
                return dblVal;
            if (bool.TryParse(text, out bool boolVal))
                return boolVal;

            return text;
        }
    }
}
