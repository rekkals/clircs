namespace Clircs.Core.Tests;

internal sealed class TestSuite
{
    private readonly List<(string Name, Func<ValueTask> Test)> _tests = [];

    public void Add(string name, Action test) => _tests.Add((name, () =>
    {
        test();
        return ValueTask.CompletedTask;
    }));

    public void Add(string name, Func<ValueTask> test) => _tests.Add((name, test));

    public async Task<int> RunAsync()
    {
        var failed = 0;
        var skipped = 0;
        foreach (var (name, test) in _tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS {name}");
            }
            catch (TestSkippedException exception)
            {
                skipped++;
                Console.WriteLine($"SKIP {name}");
                Console.WriteLine($"     {exception.Message}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"FAIL {name}");
                Console.WriteLine($"     {exception}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{_tests.Count - failed - skipped}/{_tests.Count - skipped} tests passed; {skipped} skipped.");
        return failed == 0 ? 0 : 1;
    }
}

internal sealed class TestSkippedException : Exception
{
    public TestSkippedException(string message)
        : base(message)
    {
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
        }
    }

    public static void True(bool condition, string message = "Expected condition to be true.")
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message = "Expected condition to be false.") => True(!condition, message);

    public static T Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name} to be thrown.");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name} to be thrown.");
    }
}
