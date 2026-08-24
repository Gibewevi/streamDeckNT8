using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TradeDeck.Atas.Strategy;

internal sealed class MessageError
{
    [JsonProperty("code")] public string Code { get; set; } = string.Empty;
    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
}

/// <summary>
/// The universal envelope of docs/protocol.md. Field names are lowerCamelCase on the wire and the
/// bridge is strict about them — hence the explicit [JsonProperty] on every one rather than a
/// naming strategy that a later refactor could quietly change.
/// </summary>
internal sealed class BridgeMessage
{
    [JsonProperty("type")] public string Type { get; set; } = string.Empty;
    [JsonProperty("version")] public string Version { get; set; } = "1.0";
    [JsonProperty("requestId", NullValueHandling = NullValueHandling.Ignore)] public string? RequestId { get; set; }
    [JsonProperty("timestamp", NullValueHandling = NullValueHandling.Ignore)] public string? Timestamp { get; set; }
    [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)] public string? Source { get; set; }
    [JsonProperty("action")] public string Action { get; set; } = string.Empty;
    [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)] public JObject? Payload { get; set; }
    [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] public JObject? Result { get; set; }
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public MessageError? Error { get; set; }

    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.None,
    };

    public string ToJson() => JsonConvert.SerializeObject(this, Settings);

    /// <summary>Returns null on anything that is not a JSON object — never throws.</summary>
    public static BridgeMessage? FromJson(string json)
    {
        try { return JsonConvert.DeserializeObject<BridgeMessage>(json, Settings); }
        catch (JsonException) { return null; }
    }

    public static BridgeMessage Response(string? requestId, string action, bool success, object? result = null)
    {
        var payload = result == null ? new JObject() : JObject.FromObject(result);
        payload["success"] = success;

        return new BridgeMessage
        {
            Type = "response",
            RequestId = requestId,
            Source = "addon",
            Action = action,
            Timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Result = payload,
        };
    }

    public static BridgeMessage Failure(string? requestId, string action, string code, string message) => new()
    {
        Type = "response",
        RequestId = requestId,
        Source = "addon",
        Action = action,
        Timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        Result = new JObject { ["success"] = false },
        Error = new MessageError { Code = code, Message = message },
    };

    public static BridgeMessage Notify(string action, object payload) => new()
    {
        Type = "event",
        Source = "addon",
        Action = action,
        Timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        Payload = JObject.FromObject(payload),
    };

    public string? GetString(string key) => Payload?[key]?.Type == JTokenType.String ? Payload[key]!.Value<string>() : null;

    /// <summary>
    /// NEVER THROWS ON A MALFORMED VALUE — it reads as absent instead.
    ///
    /// Same rule as the bridge's TryGetInt32: an exception in the validation path cost the whole
    /// session once, which the host reconnected immediately only to make it fall again — a
    /// permanent loop out of one bad field. A decimal is truncated rather than refused, matching
    /// SimpleJson.GetInt on the NinjaTrader side exactly; the bridge has already refused it with
    /// INVALID_PAYLOAD long before it gets here, so this is a second line, not the first.
    /// </summary>
    public int? GetInt(string key)
    {
        var token = Payload?[key];
        if (token == null) return null;
        try
        {
            return token.Type switch
            {
                JTokenType.Integer => (int)token.Value<long>(),
                JTokenType.Float => (int)token.Value<double>(),
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }

    public double? GetDouble(string key)
    {
        var token = Payload?[key];
        if (token == null) return null;
        try
        {
            return token.Type is JTokenType.Integer or JTokenType.Float ? token.Value<double>() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Absent, null or malformed reads as FALSE. Deliberate: this backs the guard policy, and a
    /// payload nobody could understand must never be taken for permission to trade.
    /// </summary>
    public bool GetBool(string key)
    {
        var token = Payload?[key];
        if (token == null) return false;
        try
        {
            return token.Type switch
            {
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.String => string.Equals(token.Value<string>(), "true", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
        catch (Exception) { return false; }
    }
}
