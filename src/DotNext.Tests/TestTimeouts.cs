namespace DotNext;

/// <summary>
/// Per-test timeouts, in milliseconds, for <see cref="FactAttribute.Timeout"/> and <see cref="TheoryAttribute"/>.
/// </summary>
/// <remarks>
/// Every asynchronous test must declare a timeout so that a hang fails that test rather than stalling the whole run.
/// xUnit supports timeouts on asynchronous tests only.
/// </remarks>
internal static class TestTimeouts
{
    public const int Default = 60_000;

    public const int Long = 300_000;
}
