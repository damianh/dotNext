using System.Globalization;

namespace DotNext.Raft.FaultCampaign;

/// <summary>
/// <c>--name value</c> options. Every option takes a value; an unknown or repeated option is a usage error.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> read = new(StringComparer.Ordinal);

    internal CommandLine(ReadOnlySpan<string> args)
    {
        for (var i = 0; i < args.Length; i += 2)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length is 2)
                throw new UsageException($"expected an option, found '{name}'");

            if (i + 1 >= args.Length)
                throw new UsageException($"option {name} needs a value");

            if (!values.TryAdd(name[2..], args[i + 1]))
                throw new UsageException($"option {name} is given twice");
        }
    }

    internal string? Get(string name)
    {
        read.Add(name);
        return values.GetValueOrDefault(name);
    }

    internal string Require(string name)
        => Get(name) ?? throw new UsageException($"option --{name} is required");

    internal int GetInt32(string name, int defaultValue, int min = int.MinValue, int max = int.MaxValue)
    {
        if (Get(name) is not { } text)
            return defaultValue;

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new UsageException($"option --{name} must be an integer in [{min}, {max}], found '{text}'");

        return value;
    }

    internal double GetDouble(string name, double defaultValue, double min, double max)
    {
        if (Get(name) is not { } text)
            return defaultValue;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < min || value > max)
            throw new UsageException($"option --{name} must be a number in [{min}, {max}], found '{text}'");

        return value;
    }

    internal T GetChoice<T>(string name, T defaultValue, params ReadOnlySpan<(string Name, T Value)> choices)
    {
        if (Get(name) is not { } text)
            return defaultValue;

        foreach (var (choice, value) in choices)
        {
            if (string.Equals(choice, text, StringComparison.Ordinal))
                return value;
        }

        var names = new List<string>();
        foreach (var choice in choices)
            names.Add(choice.Name);

        throw new UsageException($"option --{name} must be one of {string.Join(", ", names)}, found '{text}'");
    }

    /// <summary>
    /// Fails on an option that no <c>Get</c> call asked for, so a typo is not silently ignored.
    /// </summary>
    internal void RequireAllRead()
    {
        foreach (var name in values.Keys)
        {
            if (!read.Contains(name))
                throw new UsageException($"unknown option --{name}");
        }
    }
}

internal sealed class UsageException(string message) : Exception(message);
