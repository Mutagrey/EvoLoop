using System.Net;
using System.Text.Json;
using Agent.Core;
using Agent.Providers;
using static TestAssert;

internal static class ProviderAndParserTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("OpenAI provider retries without response_format when rejected", TestOpenAiProviderResponseFormatFallback),
        ("Custom provider retries system prompt as user message when rejected", TestCustomProviderSystemPromptFallback),
        ("Ollama provider sends native chat payload with think disabled", TestOllamaProviderPayload),
        ("Provider rooted paths resolve against base URL in offline strict mode", TestRootedProviderPathUsesBaseUrlHost),
        ("Plain text recovery parser recovers Action Arguments", TestPlainTextRecoveryParser),
        ("Tool error result becomes structured error message", TestToolErrorResultMessage)
    };

static async Task TestOpenAiProviderResponseFormatFallback()
{
    var handler = new RecordingHttpHandler((index, _) => index == 1
        ? new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"response_format rejected\"}")
        }
        : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"model\":\"fake\",\"choices\":[{\"message\":{\"content\":\"{\\\"type\\\":\\\"final\\\",\\\"message\\\":\\\"ok\\\"}\"}}]}")
        });

    var config = new AgentConfig
    {
        Api = new ApiConfig
        {
            BaseUrl = "http://localhost:8000",
            OpenAiCompatiblePath = "/v1/chat/completions",
            PreferJsonResponseFormat = true,
            ResponseFormatFallbackWithoutJson = true,
            SystemPromptMode = "system"
        }
    };

    var client = new OpenAiCompatibleClient(new HttpClient(handler), config, new ModelProfileConfig { Provider = "openai", Model = "fake" });
    var result = await client.CompleteAsync(CreateProviderRequest(), CancellationToken.None);

    Assert(result.Content.Contains("\"ok\"", StringComparison.Ordinal), "Expected fallback request to return successful content.");
    Assert(handler.RequestBodies.Count == 2, "Expected response_format rejection to trigger one retry.");
    using var first = JsonDocument.Parse(handler.RequestBodies[0]);
    using var second = JsonDocument.Parse(handler.RequestBodies[1]);
    Assert(first.RootElement.TryGetProperty("response_format", out var firstFormat) && firstFormat.ValueKind == JsonValueKind.Object, "Expected first OpenAI request to include response_format.");
    Assert(!second.RootElement.TryGetProperty("response_format", out var secondFormat) || secondFormat.ValueKind == JsonValueKind.Null, "Expected fallback OpenAI request to omit response_format.");
}

static async Task TestCustomProviderSystemPromptFallback()
{
    var handler = new RecordingHttpHandler((index, _) => index == 1
        ? new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"unsupported system_prompt\"}")
        }
        : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"response\":\"{\\\"type\\\":\\\"final\\\",\\\"message\\\":\\\"ok\\\"}\",\"model\":\"fake\"}")
        });

    var config = new AgentConfig
    {
        Api = new ApiConfig
        {
            BaseUrl = "http://localhost:8000",
            CustomPath = "/api/chat",
            PreferJsonResponseFormat = false,
            SystemPromptMode = "system",
            SystemPromptFallbackToUserMessage = true
        }
    };

    var client = new CustomGatewayClient(new HttpClient(handler), config, new ModelProfileConfig { Provider = "custom", Model = "fake" });
    var result = await client.CompleteAsync(CreateProviderRequest(), CancellationToken.None);

    Assert(result.Content.Contains("\"ok\"", StringComparison.Ordinal), "Expected system prompt fallback to return successful content.");
    Assert(handler.RequestBodies.Count == 2, "Expected system prompt rejection to trigger one retry.");
    using var first = JsonDocument.Parse(handler.RequestBodies[0]);
    using var second = JsonDocument.Parse(handler.RequestBodies[1]);
    Assert(first.RootElement.GetProperty("system_prompt").ValueKind == JsonValueKind.String, "Expected initial custom request to use system_prompt.");
    Assert(!second.RootElement.TryGetProperty("system_prompt", out var secondPrompt) || secondPrompt.ValueKind == JsonValueKind.Null, "Expected fallback custom request to omit system_prompt.");
    var firstMessage = second.RootElement.GetProperty("messages")[0];
    Assert(firstMessage.GetProperty("role").GetString() == "user", "Expected fallback system prompt to be sent as a user message.");
    Assert(firstMessage.GetProperty("content").GetString()?.Contains("SYSTEM INSTRUCTIONS", StringComparison.Ordinal) == true, "Expected fallback user message to contain system prompt wrapper.");
}

static async Task TestOllamaProviderPayload()
{
    var handler = new RecordingHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"model\":\"qwen3.5:9b\",\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"type\\\":\\\"final\\\",\\\"message\\\":\\\"ok\\\"}\"},\"prompt_eval_count\":3,\"eval_count\":4}")
    });

    var config = new AgentConfig
    {
        Api = new ApiConfig
        {
            BaseUrl = "http://localhost:11434",
            CustomPath = "/api/chat",
            PreferJsonResponseFormat = true,
            SystemPromptMode = "system"
        }
    };

    var client = new OllamaChatClient(new HttpClient(handler), config, new ModelProfileConfig { Provider = "ollama", Model = "qwen3.5:9b" });
    var result = await client.CompleteAsync(CreateProviderRequest() with { Model = "qwen3.5:9b" }, CancellationToken.None);

    Assert(result.Content.Contains("\"ok\"", StringComparison.Ordinal), "Expected Ollama message.content to be extracted.");
    Assert(result.PromptTokens == 3, "Expected prompt eval count to map to prompt tokens.");
    Assert(result.CompletionTokens == 4, "Expected eval count to map to completion tokens.");
    using var body = JsonDocument.Parse(handler.RequestBodies.Single());
    Assert(body.RootElement.GetProperty("think").GetBoolean() == false, "Expected Ollama thinking to be disabled.");
    Assert(body.RootElement.GetProperty("format").GetString() == "json", "Expected Ollama JSON format.");
    Assert(body.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() == 256, "Expected max tokens to map to num_predict.");
}

static async Task TestRootedProviderPathUsesBaseUrlHost()
{
    var handler = new RecordingHttpHandler((_, request) =>
    {
        Assert(request.RequestUri?.Host == "localhost", "Expected rooted API path to preserve base URL host.");
        Assert(request.RequestUri?.AbsolutePath == "/api/chat", "Expected rooted API path to use requested endpoint path.");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"model\":\"fake\",\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"type\\\":\\\"final\\\",\\\"message\\\":\\\"ok\\\"}\"}}")
        };
    });

    var config = new AgentConfig
    {
        Api = new ApiConfig
        {
            BaseUrl = "http://localhost:11434",
            CustomPath = "/api/chat",
            PreferJsonResponseFormat = true
        },
        Safety = new SafetyConfig
        {
            OfflineStrictMode = true,
            AllowedNetworkHosts = new List<string> { "localhost" }
        }
    };

    var client = new OllamaChatClient(new HttpClient(handler), config, new ModelProfileConfig { Provider = "ollama", Model = "fake" });
    var result = await client.CompleteAsync(CreateProviderRequest(), CancellationToken.None);
    Assert(result.Content.Contains("\"ok\"", StringComparison.Ordinal), "Expected request to pass offline strict host check.");
}

static ModelTurnRequest CreateProviderRequest()
    => new(
        "reasoning",
        "fake",
        "system rules",
        new[] { new ModelMessage("user", "{\"type\":\"final\",\"message\":\"ok\"}") },
        0.1,
        256);

static Task TestPlainTextRecoveryParser()
{
    var parsed = PlainTextRecoveryParser.Parse(
        "Action: echo\nArguments: {\"value\":\"ok\"}",
        new[] { "echo" });

    var call = parsed.ToolCalls.Single();
    Assert(call.Name.Value == "echo", "Expected plain-text recovery to find tool name.");
    Assert(ToolArgumentReader.GetString(call.Arguments, "value") == "ok", "Expected plain-text recovery to parse arguments.");
    Assert(parsed.ToolCallingMode == ToolCallingMode.PlainTextRecoveryFallback, "Expected plain-text recovery mode marker.");
    return Task.CompletedTask;
}

static Task TestToolErrorResultMessage()
{
    using var doc = JsonDocument.Parse("{\"value\":\"bad\"}");
    var call = new ToolCallBlock(new ToolCallId("call_err"), new ToolName("echo"), doc.RootElement.Clone(), "test");
    var message = ToolResultMessage.FromToolResult(call, new ToolResult(false, "failed", StdErr: "boom"));
    Assert(message.IsError, "Expected failed tool result to be marked as error.");
    Assert(message.ToolCallId.Value == "call_err", "Expected error result to preserve tool call id.");
    Assert(message.Content.StdErr == "boom", "Expected stderr to be preserved in structured content.");
    return Task.CompletedTask;
}
}
