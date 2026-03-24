using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamDeckBridge.Models;

/// <summary>
/// Universal message envelope for all communication between components.
/// </summary>
public sealed class BridgeMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public MessageError? Error { get; set; }

    public static BridgeMessage CreateError(string? requestId, string action, string code, string message)
    {
        return new BridgeMessage
        {
            Type = "response",
            RequestId = requestId,
            Source = "bridge",
            Action = action,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Result = JsonSerializer.SerializeToElement(new { success = false }),
            Error = new MessageError { Code = code, Message = message }
        };
    }

    public static readonly JsonSerializerOptions CamelCaseOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static BridgeMessage CreateEvent(string action, object payload)
    {
        return new BridgeMessage
        {
            Type = "event",
            RequestId = null,
            Source = "bridge",
            Action = action,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Payload = JsonSerializer.SerializeToElement(payload, CamelCaseOpts)
        };
    }
}

public sealed class MessageError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
