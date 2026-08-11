namespace McModpackTool.Core.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Core API and matching", CoreApiTests.RunAllAsync),
            ("Archive parser and builder", ArchiveTests.RunAllAsync),
            ("Server archive source reader", ServerArchiveSourceReaderTests.RunAllAsync),
            ("Server mod support resolver", ServerModSupportResolverTests.RunAllAsync),
            ("Compatibility analysis", CompatibilityTests.RunAllAsync),
            ("Server core catalog and installation", ServerCoreServiceTests.RunAllAsync),
            ("Server ZIP builder", ServerPackBuilderTests.RunAllAsync),
            ("Client modpack builder", ClientPackBuilderTests.RunAllAsync),
            ("Java runtime selection", JavaRuntimeTests.RunAllAsync),
            ("Game directory scanner", GameDirectoryScannerTests.RunAllAsync),
            ("Client directory scanner", ClientDirectoryScannerTests.RunAllAsync),
            ("Local sample round trips", SampleRegressionTests.RunAllAsync),
            ("Application workflow", AppWorkflowTests.RunAllAsync),
        };

        int failed = 0;
        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL  {name}\n{exception}");
            }
        }
        Console.WriteLine($"Completed: {tests.Length - failed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
