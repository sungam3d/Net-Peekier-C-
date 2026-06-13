// Tiny in-house test runner. Each test is just a method that throws on
// failure. We keep this off NuGet so it builds in any offline scratch
// environment; when you set up xUnit (recommended once you can install
// NuGet packages), each block below becomes one [Fact] and the helpers
// become Assert.Equal / Assert.True.

namespace NetPeeker.Core.Tests;

public static class TestRunner
{
    private static int _failed;
    private static int _passed;
    private static string _section = "";

    public static int Main()
    {
        IpCalcTests.RunAll();
        SettingsTests.RunAll();
        FormattingTests.RunAll();

        Console.WriteLine();
        Console.WriteLine($"=== {_passed} passed, {_failed} failed ===");
        return _failed == 0 ? 0 : 1;
    }

    public static void Section(string name)
    {
        _section = name;
        Console.WriteLine($"\n[{name}]");
    }

    public static void Test(string name, Action body)
    {
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  ok    {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
        }
    }

    public static void Eq<T>(T expected, T actual, string? what = null)
    {
        if (!Equals(expected, actual))
            throw new Exception(
                $"{what ?? "value"} mismatch — expected: {expected}, actual: {actual}");
    }

    public static void EqLists<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? what = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (e.Count != a.Count)
            throw new Exception(
                $"{what ?? "list"} length mismatch — expected {e.Count}, got {a.Count}\n" +
                $"        expected = [{string.Join(", ", e)}]\n" +
                $"        actual   = [{string.Join(", ", a)}]");
        for (int i = 0; i < e.Count; i++)
        {
            if (!Equals(e[i], a[i]))
                throw new Exception(
                    $"{what ?? "list"} differs at index {i} — expected {e[i]}, got {a[i]}");
        }
    }

    public static void True(bool cond, string what)
    {
        if (!cond) throw new Exception($"expected true: {what}");
    }

    public static void False(bool cond, string what)
    {
        if (cond) throw new Exception($"expected false: {what}");
    }
}
