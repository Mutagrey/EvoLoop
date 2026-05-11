using Agent.Core;
using Agent.Providers;
using Agent.Storage;
using Agent.Tools;

namespace Agent.Cli;

internal sealed class AgentExecutionHost : IDisposable
{
    private readonly ModelClientRouter? _liveRouter;

    private AgentExecutionHost(
        ModelClientRouter? liveRouter,
        IAgentLoop loop,
        IReadOnlyList<ITool> tools,
        IWorkspaceMemoryStore memoryStore,
        IPatchService patchService)
    {
        _liveRouter = liveRouter;
        Loop = loop;
        Tools = tools;
        MemoryStore = memoryStore;
        PatchService = patchService;
    }

    public IAgentLoop Loop { get; }
    public IReadOnlyList<ITool> Tools { get; }
    public IWorkspaceMemoryStore MemoryStore { get; }
    public IPatchService PatchService { get; }

    public static AgentExecutionHost Create(CliRuntimeContext context)
    {
        ModelClientRouter? liveRouter = null;
        IModelClientRouter modelRouter;
        if (context.Capabilities.CanRunAgentTasks)
        {
            liveRouter = new ModelClientRouter(context.Config);
            modelRouter = liveRouter;
        }
        else
        {
            modelRouter = new DisabledModelClientRouter(
                $"Model execution is unavailable because the agent is running in '{context.Capabilities.ModeLabel}' mode. Run 'agent doctor' to inspect gateway and environment status.");
        }

        var tools = ToolCatalog.CreateDefaultTools();
        var patchService = new WorkspacePatchService();
        IEventLog eventLog = context.Capabilities.WorkspaceWritable
            ? new JsonlEventLog(context.Workspace)
            : NullEventLog.Instance;
        var searchService = new HybridSearchService(modelRouter, context.Config, context.Workspace);
        var contextFactory = new DefaultToolContextFactory(context.Config, searchService, patchService, eventLog, context.Capabilities);
        var policy = new DefaultPolicyEngine(tools, context.Config);
        var approval = new ConsoleApprovalService(context.Renderer);
        IEventStore eventStore = context.Capabilities.WorkspaceWritable
            ? new HybridEventStore(context.Workspace)
            : NullEventStore.Instance;
        IWorkspaceMemoryStore memoryStore = context.Config.Runtime.MemoryEnabled && context.Capabilities.WorkspaceWritable
            ? new WorkspaceMemoryStore(context.Workspace, context.Config)
            : NullWorkspaceMemoryStore.Instance;
        var loop = new ReActAgentLoop(modelRouter, tools, policy, approval, eventStore, contextFactory, context.Config, memoryStore);

        return new AgentExecutionHost(liveRouter, loop, tools, memoryStore, patchService);
    }

    public void Dispose() => _liveRouter?.Dispose();
}
