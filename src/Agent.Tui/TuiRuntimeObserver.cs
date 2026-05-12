using Agent.Core;

namespace Agent.Tui;

internal sealed class TuiRuntimeObserver : IAgentRunObserver
{
    private readonly TuiApp _app;

    public TuiRuntimeObserver(TuiApp app)
    {
        _app = app;
    }

    public Task OnEventAsync(AgentRunEvent evt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled(ct);
        }

        _app.RecordRuntimeEvent(evt);
        return Task.CompletedTask;
    }
}
