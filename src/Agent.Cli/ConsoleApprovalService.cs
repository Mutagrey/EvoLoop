using Agent.Core;

namespace Agent.Cli;

internal sealed class ConsoleApprovalService : IApprovalService
{
    private readonly AnsiRenderer _renderer;

    public ConsoleApprovalService(AnsiRenderer renderer)
    {
        _renderer = renderer;
    }

    public Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        _renderer.WritePanel(
            "Approval Required",
            $"Tool: {request.ToolName}\nReason: {request.Reason}\nArguments: {request.ArgumentsPreview}\n\nApprove? (y/N)");

        while (true)
        {
            Console.Write("approve> ");
            var input = Console.ReadLine();
            if (input is null)
            {
                return Task.FromResult(false);
            }

            input = input.Trim();
            if (input.Equals("y", StringComparison.OrdinalIgnoreCase) || input.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(true);
            }

            if (input.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(input))
            {
                return Task.FromResult(false);
            }
        }
    }
}

