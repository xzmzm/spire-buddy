using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SpireBuddy.Runtime;

internal sealed class ModelClient
{
    readonly HttpClient http;
    internal ModelClient(HttpClient http) => this.http = http;
    internal async Task<JsonNode> Request(JsonObject settings, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, settings.Text("base_url").TrimEnd('/') + path);
        var key = settings.Text("api_key");
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null) request.Content = new StringContent(body.WriteString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 400 or 413 or 422 &&
                new[] { "context_length", "context window", "maximum context", "context limit", "too many tokens", "max_tokens", "prompt is too long" }
                    .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                throw new ContextLimitException();
            throw new InvalidOperationException($"API request failed ({(int)response.StatusCode}). Check endpoint, model and credentials.");
        }
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("API returned empty JSON.");
    }
    internal static JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
    internal async Task<JsonObject> Complete(JsonObject settings, JsonArray history, JsonArray tools, string cacheKey, CancellationToken ct)
    {
        bool responses = settings.Text("api_type") == "responses";
        var payload = (JsonArray)history.DeepClone()!;
        if (!responses)
            // The developer role is Responses-only; chat completions providers such as
            // Z.ai reject it with "Incorrect role information" (error 1214).
            foreach (var message in payload.OfType<JsonObject>().ToList())
                if (message.Text("role") == "developer") message["role"] = "system";
        var body = new JsonObject { ["model"] = settings.Text("model"), [responses ? "input" : "messages"] = payload };
        if (tools.Count > 0)
        {
            body["tools"] = responses ? tools.DeepClone() : new JsonArray(tools.OfType<JsonObject>().Select(t => (JsonNode)new JsonObject { ["type"] = "function", ["function"] = new JsonObject(t.Where(p => p.Key != "type").Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))) }).ToArray());
            // Read-only inspections may be batched in one turn; the runtime executes
            // them serially and rejects a premature take_action bundled with them.
            body["parallel_tool_calls"] = true;
        }
        var effort = settings.Text("reasoning_effort");
        if (effort.Length > 0 && effort != "default") body[responses ? "reasoning" : "reasoning_effort"] = responses ? new JsonObject { ["effort"] = effort } : JsonValue.Create(effort);
        body["prompt_cache_key"] = cacheKey;
        if (settings.Text("model").StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase)) body["prompt_cache_options"] = new JsonObject { ["ttl"] = "30m" };
        if (responses) { body["store"] = false; body["include"] = new JsonArray("reasoning.encrypted_content"); }
        var response = await Request(settings, responses ? "/responses" : "/chat/completions", body, ct);
        var calls = new JsonArray(); var text = new StringBuilder();
        if (responses)
        {
            foreach (var item in response["output"].Items())
            {
                history.Add(item.DeepClone());
                if (item.Text("type") == "function_call") calls.Add(new JsonObject { ["id"] = item.Text("call_id"), ["name"] = item.Text("name"), ["arguments"] = item.Text("arguments") });
                foreach (var content in item["content"].Items()) if (content.Text("type") == "output_text") text.Append(content.Text("text"));
            }
        }
        else
        {
            var message = response["choices"]?[0]?["message"] ?? throw new InvalidOperationException("API response has no message.");
            history.Add(message.DeepClone()); text.Append(message.Text("content"));
            foreach (var call in message["tool_calls"].Items()) calls.Add(new JsonObject { ["id"] = call.Text("id"), ["name"] = call["function"].Text("name"), ["arguments"] = call["function"].Text("arguments") });
        }
        return new JsonObject { ["calls"] = calls, ["text"] = text.ToString(), ["usage"] = response["usage"]?.DeepClone() };
    }
    internal static void Result(JsonArray history, JsonObject settings, string id, JsonNode result)
    {
        history.Add(settings.Text("api_type") == "responses"
            ? new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = result.WriteString() }
            : new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result.WriteString() });
    }
    internal static JsonObject Tool(string name, string description, params string[] arguments)
    {
        var properties = new JsonObject(); foreach (var arg in arguments) properties[arg] = new JsonObject { ["type"] = "string" };
        return new JsonObject { ["type"] = "function", ["name"] = name, ["description"] = description, ["strict"] = true,
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(arguments.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()), ["additionalProperties"] = false } };
    }
}

internal sealed class ContextLimitException : InvalidOperationException
{
    internal ContextLimitException() : base("The model context limit was exceeded.") { }
}
