using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Headroom.Core.Tests;

/// <summary>Marks a public static method as a test case.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    public TestAttribute(string? name = null) => Name = name;

    public string? Name { get; }
}

public sealed class AssertionFailedException : Exception
{
    public AssertionFailedException(string message) : base(message) { }
}

/// <summary>
/// A deliberately tiny assertion library.
/// </summary>
/// <remarks>
/// Headroom takes no NuGet dependencies anywhere, including here. A test suite
/// that needs a package restore is a test suite people skip when the restore
/// breaks, and the whole point of this project is that <c>dotnet run</c> works
/// on a fresh machine with nothing but the SDK.
/// </remarks>
public static class Check
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new AssertionFailedException(message);
    }

    public static void False(bool condition, string message = "expected false")
    {
        if (condition) throw new AssertionFailedException(message);
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionFailedException($"{message ?? "values differ"}\n  expected: {Format(expected)}\n  actual:   {Format(actual)}");
    }

    public static void Close(double expected, double? actual, double tolerance = 0.001d, string? message = null)
    {
        if (actual is null || Math.Abs(expected - actual.Value) > tolerance)
            throw new AssertionFailedException($"{message ?? "values differ"}\n  expected: {expected}\n  actual:   {Format(actual)}");
    }

    public static void Null(object? value, string message = "expected null")
    {
        if (value is not null) throw new AssertionFailedException($"{message}, got {Format(value)}");
    }

    public static T NotNull<T>(T? value, string message = "expected a value") where T : class
    {
        if (value is null) throw new AssertionFailedException(message);
        return value;
    }

    public static T NotNullValue<T>(T? value, string message = "expected a value") where T : struct
    {
        if (value is null) throw new AssertionFailedException(message);
        return value.Value;
    }

    public static void Contains(string needle, string? haystack, string? message = null)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            throw new AssertionFailedException($"{message ?? "substring missing"}\n  needle:   {needle}\n  haystack: {haystack ?? "<null>"}");
    }

    public static void Count<T>(int expected, IReadOnlyCollection<T> items, string? message = null)
    {
        if (items.Count != expected)
            throw new AssertionFailedException(
                $"{message ?? "wrong number of items"}\n  expected: {expected}\n  actual:   {items.Count}\n  items:    {string.Join(", ", items.Select(i => Format(i)))}");
    }

    private static string Format(object? value) => value switch
    {
        null => "<null>",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? "<null>",
    };
}

/// <summary>Fixed instants for tests, always parsed culture-invariantly.</summary>
public static class Moment
{
    public static DateTimeOffset At(string iso) => DateTimeOffset.Parse(
        iso,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}

public static class TestRunner
{
    public static async Task<int> RunAsync(Assembly assembly, string? filter)
    {
        var cases = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => (
                Name: $"{m.DeclaringType!.Name}.{m.GetCustomAttribute<TestAttribute>()!.Name ?? m.Name}",
                Method: m))
            .Where(c => filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var stopwatch = Stopwatch.StartNew();
        var failures = new List<(string Name, Exception Error)>();

        foreach (var (name, method) in cases)
        {
            try
            {
                var result = method.Invoke(null, Array.Empty<object>());
                if (result is Task task) await task.ConfigureAwait(false);
                Console.WriteLine($"  pass  {name}");
            }
            catch (Exception e)
            {
                var error = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                failures.Add((name, error));
                Console.WriteLine($"  FAIL  {name}");
            }
        }

        stopwatch.Stop();
        Console.WriteLine();

        if (cases.Count == 0)
        {
            Console.WriteLine(filter is null
                ? "No tests were discovered. That is a failure, not an empty pass."
                : $"No tests matched the filter \"{filter}\". That is a failure, not an empty pass.");
            return 1;
        }

        foreach (var (name, error) in failures)
        {
            Console.WriteLine($"FAILED {name}");
            Console.WriteLine($"  {error.Message}");
            if (error is not AssertionFailedException && error.StackTrace is { } stack)
                Console.WriteLine(string.Join('\n', stack.Split('\n').Take(4)));
            Console.WriteLine();
        }

        Console.WriteLine(failures.Count == 0
            ? $"All {cases.Count} tests passed in {stopwatch.ElapsedMilliseconds} ms."
            : $"{failures.Count} of {cases.Count} tests FAILED in {stopwatch.ElapsedMilliseconds} ms.");

        return failures.Count == 0 ? 0 : 1;
    }
}

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        // `dotnet run` forwards options it does not recognise straight through to
        // the app, so a stray --nologo would otherwise be taken as a name filter
        // and silently match nothing.
        var filter = Array.Find(args, a => !a.StartsWith('-'));
        return TestRunner.RunAsync(typeof(Program).Assembly, filter);
    }
}
