internal static class ReActLoopTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } = ReActLoopRecoveryTests.All
        .Concat(ReActLoopLimitTests.All)
        .Concat(ReActLoopBasicTests.All)
        .Concat(NativeToolCallTests.All)
        .Concat(ReActLoopContextTests.All)
        .ToArray();
}
