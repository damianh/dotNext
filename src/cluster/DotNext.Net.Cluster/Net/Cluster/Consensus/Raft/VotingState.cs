using System.Runtime.InteropServices;

namespace DotNext.Net.Cluster.Consensus.Raft;

using ReplicationUtils;

/// <summary>
/// Counts the responses of a vote or pre-vote round over the configuration that the round started with (#146).
/// </summary>
/// <remarks>
/// The round is decided as soon as the granted responses form a majority, or the remaining members cannot form one,
/// so a silent member doesn't hold the round until its request times out.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
internal struct VotingState(int count)
{
    private readonly int majority = ReplicationState.GetMajority(count);
    private int granted, denied;

    public void Grant() => granted++;

    public void Deny() => denied++;

    public readonly bool IsWon => granted >= majority;

    public readonly bool IsLost => count - denied < majority;

    public readonly bool IsDecided => IsWon || IsLost;

    // The difference between granted and denied responses, as reported before #146
    public readonly int Weight => granted - denied;

    // The responses that arrive after the decision are ignored, their failures must not go unobserved.
    internal static void IgnoreRemaining(ReadOnlySpan<Task> responses)
    {
        foreach (var response in responses)
        {
            if (!response.IsCompleted)
            {
                response.ContinueWith(
                    static task => task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else if (response.IsFaulted)
            {
                _ = response.Exception;
            }
        }
    }
}
