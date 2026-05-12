using Agent.Core;

namespace Agent.Tui;

internal sealed class TuiApprovalService : IApprovalService
{
    private readonly TuiApp _app;
    private readonly Func<ApprovalRequest, CancellationToken, Task<bool>> _requestApproval;

    public TuiApprovalService(
        TuiApp app,
        Func<ApprovalRequest, CancellationToken, Task<bool>>? requestApproval = null)
    {
        _app = app;
        _requestApproval = requestApproval ?? RejectUntilInteractivePromptExistsAsync;
    }

    public async Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        _app.RecordApprovalRequest(request);
        var approved = await _requestApproval(request, ct);
        _app.RecordApprovalResult(request.ToolName, approved);
        return approved;
    }

    private static Task<bool> RejectUntilInteractivePromptExistsAsync(ApprovalRequest request, CancellationToken ct)
    {
        return Task.FromResult(false);
    }
}
