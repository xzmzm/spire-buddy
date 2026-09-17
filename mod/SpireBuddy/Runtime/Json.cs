using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

internal static class Json
{
    // The default encoder escapes quotes as \u0022 and HTML-sensitive characters as
    // \u003C/\u0026, tripling those characters' size in every model request and trace.
    // Relaxed escaping stays valid JSON and keeps non-ASCII text readable.
    internal static readonly JsonSerializerOptions Write = new(JsonSerializerDefaults.General)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string WriteString(this JsonNode node) => node.ToJsonString(Write);
}
