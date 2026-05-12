using System.Reflection;
using System.Runtime.Loader;

AssemblyLoadContext.Default.Resolving += ResolveFromOutput;

var tests = new List<(string Name, Func<Task> Run)>();
tests.AddRange(CliTests.All);
tests.AddRange(TuiTests.All);
tests.AddRange(PolicyTests.All);
tests.AddRange(RuntimeCapabilityTests.All);
tests.AddRange(ReActLoopTests.All);
tests.AddRange(ProviderAndParserTests.All);
tests.AddRange(SearchMemoryPatchTests.All);
tests.AddRange(SafetySearchTests.All);

if (args.Length > 0)
{
    tests = tests
        .Where(test => args.Any(filter =>
            test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    if (tests.Count == 0)
    {
        Console.Error.WriteLine($"No tests matched filter: {string.Join(", ", args)}");
        return 1;
    }
}

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"[PASS] {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"[FAIL] {test.Name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"Tests failed: {failed}");
    return 1;
}

Console.WriteLine("All tests passed.");
return 0;

static Assembly? ResolveFromOutput(AssemblyLoadContext context, AssemblyName name)
{
    if (string.IsNullOrWhiteSpace(name.Name))
    {
        return null;
    }

    var candidate = Path.Combine(AppContext.BaseDirectory, $"{name.Name}.dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
}
