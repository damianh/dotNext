namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogNodeStateTests : Test
{
    [Fact]
    public static void DirectoryBarrierFailureIsRetriedAfterReopen()
    {
        var location = GetTempPath();
        var options = new WriteAheadLog.Options { Location = location };
        var statePath = Path.Combine(location, "state");
        var barrierCount = 0;

        void FlushDirectory(DirectoryInfo _)
        {
            if (++barrierCount is 1)
                throw new IOException("Injected directory barrier failure.");
        }

        Throws<IOException>(() => new WriteAheadLog(options, IStateMachine.CreateNoOp(), FlushDirectory));
        True(File.Exists(statePath));

        using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp(), FlushDirectory);
        Equal(2, barrierCount);
        True(File.Exists(statePath));
    }
}
