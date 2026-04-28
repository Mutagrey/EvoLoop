using Agent.Core;

namespace Agent.Tools;

public static class ToolCatalog
{
    public static IReadOnlyList<ITool> CreateDefaultTools()
    {
        return new ITool[]
        {
            new FsListTool(),
            new FsReadTool(),
            new FsWriteTool(),
            new FsPatchTool(),
            new FsDeleteTool(),
            new WorkspaceUndoTool(),
            new WorkspaceSnapshotDiffTool(),
            new GitStatusTool(),
            new GitDiffTool(),
            new GitLogTool(),
            new GitShowTool(),
            new GitAddTool(),
            new GitCommitTool(),
            new ExecShellTool(),
            new SearchLexicalTool(),
            new SearchSemanticTool()
        };
    }
}
