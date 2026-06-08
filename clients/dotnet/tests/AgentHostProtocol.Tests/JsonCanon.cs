#nullable enable

using System;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Microsoft.AgentHostProtocol.Tests;

/// <summary>
/// Canonical JSON string for structural comparison: object keys sorted, and
/// <c>null</c>-valued object members dropped (so an omitted optional field
/// equals an explicit null — matching the Go/TS conformance harnesses).
/// </summary>
internal static class JsonCanon
{
    public static string Of(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(element, sb);
        return sb.ToString();
    }

    private static void Write(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject()
                             .Where(p => p.Value.ValueKind != JsonValueKind.Null)
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    Write(p.Value, sb);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Write(item, sb);
                }

                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(e.GetString()));
                break;
            case JsonValueKind.Number:
                sb.Append(e.GetRawText());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }
}
