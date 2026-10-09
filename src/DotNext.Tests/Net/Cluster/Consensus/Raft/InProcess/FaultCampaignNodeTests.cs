using DotNext.Raft.FaultCampaign.Driver;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

/// <summary>
/// The failure signals that a node process of the real-process fault campaign (<c>src/DotNext.Raft.FaultCampaign</c>)
/// logs itself, as the driver classifies them.
/// </summary>
public sealed class FaultCampaignNodeTests : Test
{
    // A write that fails with an unexpected exception on a live node is logged as Critical and fails the run (exit 6);
    // on a node that SIGTERM is stopping, it is logged as Warning and only reported.
    [Theory]
    [InlineData("""{"EventId":0,"LogLevel":"Critical","Category":"FaultCampaign.Node","Message":"The write failed with an unexpected exception","Exception":"System.InvalidOperationException: boom"}""", "Unexpected")]
    [InlineData("""{"EventId":0,"LogLevel":"Warning","Category":"FaultCampaign.Node","Message":"The write failed with an unexpected exception","Exception":"System.ObjectDisposedException: Cannot access a disposed object."}""", "Unclassified")]
    [InlineData("""{"EventId":0,"LogLevel":"Warning","Category":"FaultCampaign.Node","Message":"The write failed with an unexpected exception","Exception":"DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLog+InternalException: failed"}""", "Unexpected")]
    public static void FailedWritesAreClassified(string line, string expected)
        => Equal(expected, LogClassifier.Classify(line)?.Class.ToString());
}
