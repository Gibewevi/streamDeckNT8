using System;
using System.Collections.Generic;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Models
{
    /// <summary>
    /// Universal message envelope matching the bridge protocol V1.
    /// Uses SimpleJson for NT8 NinjaScript compatibility (no Newtonsoft).
    /// </summary>
    public class BridgeMessage
    {
        public string Type { get; set; }
        public string Version { get; set; }
        public string RequestId { get; set; }
        public string Timestamp { get; set; }
        public string Source { get; set; }
        public string Action { get; set; }
        public Dictionary<string, object> Payload { get; set; }
        public Dictionary<string, object> Result { get; set; }
        public MessageError Error { get; set; }

        public BridgeMessage()
        {
            Type = string.Empty;
            Version = "1.0";
            Action = string.Empty;
        }

        public string ToJson()
        {
            var dict = new Dictionary<string, object>();
            dict["type"] = Type;
            dict["version"] = Version;
            if (RequestId != null) dict["requestId"] = RequestId;
            if (Timestamp != null) dict["timestamp"] = Timestamp;
            if (Source != null) dict["source"] = Source;
            dict["action"] = Action;
            if (Payload != null) dict["payload"] = Payload;
            if (Result != null) dict["result"] = Result;
            if (Error != null)
            {
                var errDict = new Dictionary<string, object>();
                errDict["code"] = Error.Code;
                errDict["message"] = Error.Message;
                dict["error"] = errDict;
            }
            return SimpleJson.Serialize(dict);
        }

        public static BridgeMessage FromJson(string json)
        {
            var dict = SimpleJson.Deserialize(json);
            if (dict == null) return null;

            var msg = new BridgeMessage
            {
                Type = SimpleJson.GetString(dict, "type") ?? string.Empty,
                Version = SimpleJson.GetString(dict, "version") ?? "1.0",
                RequestId = SimpleJson.GetString(dict, "requestId"),
                Timestamp = SimpleJson.GetString(dict, "timestamp"),
                Source = SimpleJson.GetString(dict, "source"),
                Action = SimpleJson.GetString(dict, "action") ?? string.Empty
            };

            object payloadObj;
            if (dict.TryGetValue("payload", out payloadObj) && payloadObj is Dictionary<string, object>)
                msg.Payload = (Dictionary<string, object>)payloadObj;

            object resultObj;
            if (dict.TryGetValue("result", out resultObj) && resultObj is Dictionary<string, object>)
                msg.Result = (Dictionary<string, object>)resultObj;

            object errorObj;
            if (dict.TryGetValue("error", out errorObj) && errorObj is Dictionary<string, object>)
            {
                var errDict = (Dictionary<string, object>)errorObj;
                msg.Error = new MessageError
                {
                    Code = SimpleJson.GetString(errDict, "code") ?? string.Empty,
                    Message = SimpleJson.GetString(errDict, "message") ?? string.Empty
                };
            }

            return msg;
        }

        public static BridgeMessage CreateResponse(string requestId, string action, bool success, object resultData = null)
        {
            var msg = new BridgeMessage
            {
                Type = "response",
                Version = "1.0",
                RequestId = requestId,
                Source = "addon",
                Action = action,
                Timestamp = DateTimeOffset.UtcNow.ToString("o")
            };

            if (resultData != null)
            {
                var json = SimpleJson.Serialize(resultData);
                var dict = SimpleJson.Deserialize(json);
                if (dict != null)
                {
                    dict["success"] = success;
                    msg.Result = dict;
                }
                else
                {
                    msg.Result = new Dictionary<string, object> { { "success", success } };
                }
            }
            else
            {
                msg.Result = new Dictionary<string, object> { { "success", success } };
            }

            return msg;
        }

        public static BridgeMessage CreateError(string requestId, string action, string code, string message)
        {
            return new BridgeMessage
            {
                Type = "response",
                Version = "1.0",
                RequestId = requestId,
                Source = "addon",
                Action = action,
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Result = new Dictionary<string, object> { { "success", false } },
                Error = new MessageError { Code = code, Message = message }
            };
        }

        public static BridgeMessage CreateEvent(string action, object payload)
        {
            var json = SimpleJson.Serialize(payload);
            var dict = SimpleJson.Deserialize(json);

            return new BridgeMessage
            {
                Type = "event",
                Version = "1.0",
                Source = "addon",
                Action = action,
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Payload = dict
            };
        }

        public string GetPayloadString(string key)
        {
            return SimpleJson.GetString(Payload, key);
        }

        public int? GetPayloadInt(string key)
        {
            return SimpleJson.GetInt(Payload, key);
        }

        public double? GetPayloadDouble(string key)
        {
            return SimpleJson.GetDouble(Payload, key);
        }
    }

    public class MessageError
    {
        public string Code { get; set; }
        public string Message { get; set; }

        public MessageError()
        {
            Code = string.Empty;
            Message = string.Empty;
        }
    }
}
