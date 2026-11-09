using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Musterdepot.Core.Commands;

namespace Musterdepot.Mcp;

/// <summary>JSON für Tool-Ergebnisse: snake_case-Felder, Enums als snake_case-Text, decimal verlustfrei.</summary>
public static class McpJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Status(CommandStatus status) => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
}

/// <summary>Einheitlicher Umschlag jedes Tool-Ergebnisses: <c>{ status, message, data, … }</c>.</summary>
public sealed record ToolEnvelope
{
    /// <summary>ok, error, denied, not_found, invalid_arguments oder ein Befehlsstatus (executed, previewed, pending_approval, rejected, duplicate, failed).</summary>
    public required string Status { get; init; }
    public string? Message { get; init; }
    public object? Data { get; init; }
    public string? AuditId { get; init; }
    public string? ApprovalId { get; init; }
    public bool? RequiresHumanApproval { get; init; }
    public string? IdempotencyKey { get; init; }

    public CallToolResult ToResult(bool isError)
    {
        var element = JsonSerializer.SerializeToElement(this, McpJson.Options);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
            IsError = isError ? true : null,
        };
    }
}
