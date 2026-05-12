using System.Text.Json;
using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;
using static TestAssert;

internal static class NativeToolCallTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = new List<(string, Func<Task>)>
    {
        ("Native non-streaming tool call executes and appends role tool result", TestNativeNonStreamingToolCallExecutes),
        ("Native multiple tool calls execute in order", TestNativeMultipleToolCallsExecuteInOrder),
        ("Streaming native tool call accumulates fragmented arguments", TestStreamingToolCallAccumulation),
        ("JSON-ReAct fallback normalizes tool call through adapter", TestJsonReActFallbackNormalizesToolCall)
    };

static async Task TestNativeNonStreamingToolCallExecutes()
{
    var config = new AgentConfig
    {
        Models = new Dictionary<string, ModelProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["reasoning"] = new()
            {
                Provider = "openai",
                Model = "fake-native",
                ToolCallingMode = ToolCallingMode.NativeNonStreamingTools
            }
        }
    };

    var raw = """
              {
                "model":"fake-native",
                "choices":[
                  {
                    "message":{
                      "content":null,
                      "tool_calls":[
                        {
                          "id":"call_123",
                          "type":"function",
                          "function":{
                            "name":"echo",
                            "arguments":"{\"value\":\"ok\"}"
                          }
                        }
                      ]
                    }
                  }
                ],
                "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}
              }
              """;

    var responses = new Queue<ModelAdapterTurnResult>();
    responses.Enqueue(OpenAiCompatibleToolCallParser.ParseNonStreaming(raw, "fake-native", ToolCallingMode.NativeNonStreamingTools));
    responses.Enqueue(new ModelAdapterTurnResult(
        new AssistantMessage(new AssistantContentBlock[] { new TextBlock("native complete") }, "native complete", ToolCallingMode.NativeNonStreamingTools, AssistantMessageKind.Final),
        "fake-native",
        ToolCallingMode: ToolCallingMode.NativeNonStreamingTools));

    var adapter = new FakeModelAdapter(responses);
    var router = new FakeModelRouter(new FakeModelClient(new Queue<ModelTurnResult>()), "fake-native");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config,
        modelAdapterRouter: new FakeModelAdapterRouter(adapter));

    var result = await loop.RunAsync(new AgentRunRequest("run native tool", Path.GetTempPath(), "reasoning", 5), CancellationToken.None);
    Assert(result.Success, "Expected native tool loop to complete.");
    Assert(result.StepTrace.Count == 1, "Expected one native tool execution.");
    Assert(adapter.SeenRequests.Count >= 2, "Expected second model request after tool result.");
    Assert(adapter.SeenRequests[1].Messages.Any(m =>
        m.Role.Equals("tool", StringComparison.OrdinalIgnoreCase) &&
        m.ToolCallId == "call_123" &&
        m.Content.Contains("ok", StringComparison.OrdinalIgnoreCase)),
        "Expected role=tool result with matching tool_call_id to be appended for native mode.");
}

static async Task TestNativeMultipleToolCallsExecuteInOrder()
{
    var config = new AgentConfig
    {
        Models = new Dictionary<string, ModelProfileConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["reasoning"] = new()
            {
                Provider = "openai",
                Model = "fake-native",
                ToolCallingMode = ToolCallingMode.NativeNonStreamingTools
            }
        }
    };

    var raw = """
              {
                "model":"fake-native",
                "choices":[
                  {
                    "message":{
                      "content":null,
                      "tool_calls":[
                        {"id":"call_a","type":"function","function":{"name":"echo","arguments":"{\"value\":\"a\"}"}},
                        {"id":"call_b","type":"function","function":{"name":"echo","arguments":"{\"value\":\"b\"}"}}
                      ]
                    }
                  }
                ]
              }
              """;

    var responses = new Queue<ModelAdapterTurnResult>();
    responses.Enqueue(OpenAiCompatibleToolCallParser.ParseNonStreaming(raw, "fake-native", ToolCallingMode.NativeNonStreamingTools));
    responses.Enqueue(new ModelAdapterTurnResult(
        new AssistantMessage(new AssistantContentBlock[] { new TextBlock("complete") }, "complete", ToolCallingMode.NativeNonStreamingTools, AssistantMessageKind.Final),
        "fake-native",
        ToolCallingMode: ToolCallingMode.NativeNonStreamingTools));

    var adapter = new FakeModelAdapter(responses);
    var router = new FakeModelRouter(new FakeModelClient(new Queue<ModelTurnResult>()), "fake-native");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config,
        modelAdapterRouter: new FakeModelAdapterRouter(adapter));

    var result = await loop.RunAsync(new AgentRunRequest("run two native tools", Path.GetTempPath(), "reasoning", 6), CancellationToken.None);
    Assert(result.Success, "Expected multi-tool native turn to complete.");
    Assert(result.StepTrace.Count == 2, "Expected both native tool calls to execute.");
    Assert(result.StepTrace[0].Output.Contains("a", StringComparison.OrdinalIgnoreCase), "Expected first tool result first.");
    Assert(result.StepTrace[1].Output.Contains("b", StringComparison.OrdinalIgnoreCase), "Expected second tool result second.");
    Assert(adapter.SeenRequests.Last().Messages.Count(m => m.Role.Equals("tool", StringComparison.OrdinalIgnoreCase)) == 2,
        "Expected both native tool results to be appended before the final model turn.");
}

static Task TestStreamingToolCallAccumulation()
{
    var stream = string.Join('\n', new[]
    {
        "data: {\"model\":\"fake-stream\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_stream\",\"type\":\"function\",\"function\":{\"name\":\"echo\",\"arguments\":\"{\\\"val\"}}]}}]}",
        "data: {\"model\":\"fake-stream\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ue\\\":\\\"ok\\\"}\"}}]}}]}",
        "data: [DONE]"
    });

    var parsed = OpenAiCompatibleToolCallParser.ParseStreaming(stream, "fake-stream");
    var call = parsed.AssistantMessage.ToolCalls.Single();
    Assert(call.Id.Value == "call_stream", "Expected streaming tool call id to be preserved.");
    Assert(call.Name.Value == "echo", "Expected streaming tool name to be preserved.");
    Assert(ToolArgumentReader.GetString(call.Arguments, "value") == "ok", "Expected fragmented arguments to reconstruct valid JSON.");
    return Task.CompletedTask;
}

static async Task TestJsonReActFallbackNormalizesToolCall()
{
    var config = new AgentConfig();
    var responses = new Queue<ModelAdapterTurnResult>();
    responses.Enqueue(new ModelAdapterTurnResult(
        JsonReActResponseParser.Parse(
            "{\"type\":\"tool\",\"tool\":\"echo\",\"reason\":\"test\",\"arguments\":{\"value\":\"ok\"}}",
            new[] { "echo" },
            allowPlainTextRecovery: false),
        "fake-json"));
    responses.Enqueue(new ModelAdapterTurnResult(
        new AssistantMessage(new AssistantContentBlock[] { new TextBlock("json complete") }, "{\"type\":\"final\",\"message\":\"json complete\"}", ToolCallingMode.JsonReActFallback, AssistantMessageKind.Final),
        "fake-json"));

    var adapter = new FakeModelAdapter(responses);
    var router = new FakeModelRouter(new FakeModelClient(new Queue<ModelTurnResult>()), "fake-json");
    var loop = new ReActAgentLoop(
        router,
        new ITool[] { new EchoTool() },
        new DefaultPolicyEngine(config),
        new AutoApproveService(true),
        new InMemoryEventStore(),
        new DefaultToolContextFactory(config, new NullSearchService()),
        config,
        modelAdapterRouter: new FakeModelAdapterRouter(adapter));

    var result = await loop.RunAsync(new AgentRunRequest("run json fallback", Path.GetTempPath(), "reasoning", 5), CancellationToken.None);
    Assert(result.Success, "Expected JSON-ReAct fallback adapter result to complete.");
    Assert(result.StepTrace.Count == 1, "Expected one tool execution from normalized JSON fallback.");
}
}
