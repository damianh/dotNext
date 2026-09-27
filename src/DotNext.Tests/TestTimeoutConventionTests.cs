using System.Reflection;

namespace DotNext;

public sealed class TestTimeoutConventionTests : Test
{
    [Fact]
    public static void AsyncTestsDeclareTimeout()
    {
        var missing = typeof(TestTimeoutConventionTests).Assembly
            .GetTypes()
            .SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(static method => method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask))
            .Where(static method => method.GetCustomAttribute<FactAttribute>() is { Timeout: <= 0 })
            .Select(static method => $"{method.DeclaringType?.FullName}.{method.Name}")
            .ToArray();

        True(missing.Length is 0, $"Async tests must set Timeout = TestTimeouts.*: {string.Join(", ", missing)}");
    }
}
