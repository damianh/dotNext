namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

public sealed class DurableFileTests : Test
{
    [Fact]
    public static void FlushDirectoryOfExistingDirectory()
    {
        var directory = new DirectoryInfo(GetTempPath());
        directory.Create();
        File.WriteAllBytes(Path.Combine(directory.FullName, "state"), new byte[37]);

        DurableFile.FlushDirectory(directory);
    }

    [Fact]
    public static void FlushDirectoryOfMissingDirectoryFails()
    {
        var directory = new DirectoryInfo(Path.Combine(GetTempPath(), "missing"));

        Throws<IOException>(() => DurableFile.FlushDirectory(directory));
    }
}
