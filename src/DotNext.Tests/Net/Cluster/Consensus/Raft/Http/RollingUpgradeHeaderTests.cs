using System.Net;
using System.Reflection;
using DotNext.IO.Log;
using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Http;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

using static BindingFlags;

public sealed class RollingUpgradeHeaderTests : Test
{
    private const string NodeIdHeader = "X-Raft-Node-ID";
    private const string RequestIdHeader = "X-Request-ID";
    private const string TermHeader = "X-Raft-Term";
    private const string StateVersionHeader = "X-Raft-State-Version";
    private const string PrecedingRecordIndexHeader = "X-Raft-Preceding-Record-Index";
    private const string PrecedingRecordTermHeader = "X-Raft-Preceding-Record-Term";
    private const string CommitIndexHeader = "X-Raft-Commit-Index";
    private const string CountHeader = "X-Raft-Entries-Count";
    private const string LastIndexHeader = "X-Raft-Last-Index";

    private static readonly Assembly TransportAssembly = typeof(RequestJournalConfiguration).Assembly;
    private static readonly Type AppendEntriesRequestType = TransportAssembly.GetType(
        "DotNext.Net.Cluster.Consensus.Raft.Http.AppendEntriesMessage", throwOnError: true)!;
    private static readonly Type AppendEntriesResponseType = TransportAssembly.GetType(
        "DotNext.Net.Cluster.Consensus.Raft.Http.AppendEntriesMessage`2", throwOnError: true)!
        .MakeGenericType(typeof(EmptyLogEntry), typeof(EmptyLogEntry[]));
    private static readonly Type AppendEntriesResponseInterface = TransportAssembly.GetType(
        "DotNext.Net.Cluster.Consensus.Raft.Http.IHttpMessage`1", throwOnError: true)!
        .MakeGenericType(typeof(Result<ReplicationStatus>));
    private static readonly ConstructorInfo AppendEntriesRequestConstructor = AppendEntriesRequestType.GetConstructor(
        Instance | NonPublic,
        [typeof(HttpRequest), typeof(ILogEntryProducer<IRaftLogEntry>).MakeByRefType()])!;
    private static readonly FieldInfo StateVersionField = AppendEntriesRequestType.BaseType!.GetField(
        "StateVersion", Instance | NonPublic)!;
    private static readonly ConstructorInfo AppendEntriesResponseConstructor = AppendEntriesResponseType.GetConstructor(
        Instance | NonPublic,
        [typeof(ClusterMemberId), typeof(long), typeof(long), typeof(long), typeof(long), typeof(EmptyLogEntry[]), typeof(int)])!;
    private static readonly MethodInfo ParseResponseMethod = GetParseResponseMethod();

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(null, 0)]
    [InlineData("42", 42)]
    public static void StateVersionHeaderUsesDefaultOnlyWhenAbsent(string headerValue, int expected)
    {
        var message = ParseAppendEntriesRequest(headerValue);

        Equal(expected, (int)StateVersionField.GetValue(message)!);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static void MalformedStateVersionHeaderRejected()
    {
        var exception = Throws<TargetInvocationException>(() => ParseAppendEntriesRequest("invalid"));

        IsType<RaftProtocolException>(exception.InnerException);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(null, 10L)]
    [InlineData("42", 42L)]
    public static async Task LastIndexHeaderUsesDefaultOnlyWhenAbsent(string headerValue, long expected)
    {
        var result = await ParseAppendEntriesResponseAsync(headerValue);

        Equal(expected, result.Value.LastIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task MalformedLastIndexHeaderRejected()
        => await ThrowsAsync<RaftProtocolException>(ParseAppendEntriesResponseAsync("invalid"));

    private static MethodInfo GetParseResponseMethod()
    {
        var interfaceMethods = AppendEntriesResponseType.GetInterfaceMap(AppendEntriesResponseInterface).InterfaceMethods;
        var targetMethods = AppendEntriesResponseType.GetInterfaceMap(AppendEntriesResponseInterface).TargetMethods;
        var index = Array.FindIndex(interfaceMethods, static method => method.Name is "ParseResponseAsync");
        return targetMethods[index];
    }

    private static object ParseAppendEntriesRequest(string stateVersion)
    {
        var context = new DefaultHttpContext();
        var headers = context.Request.Headers;
        headers[NodeIdHeader] = new ClusterMemberId(Random.Shared).ToString();
        headers[RequestIdHeader] = "request";
        headers[TermHeader] = "1";
        headers[PrecedingRecordIndexHeader] = "10";
        headers[PrecedingRecordTermHeader] = "1";
        headers[CommitIndexHeader] = "10";
        headers[CountHeader] = "0";

        if (stateVersion is not null)
            headers[StateVersionHeader] = stateVersion;

        object[] args = [context.Request, null];
        return AppendEntriesRequestConstructor.Invoke(args);
    }

    private static async Task<Result<ReplicationStatus>> ParseAppendEntriesResponseAsync(string lastIndex)
    {
        var message = AppendEntriesResponseConstructor.Invoke(
            [new ClusterMemberId(Random.Shared), 1L, 10L, 1L, 10L, Array.Empty<EmptyLogEntry>(), 0]);

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(nameof(HeartbeatResult.Replicated)),
        };
        response.Headers.Add(TermHeader, "1");

        if (lastIndex is not null)
            response.Headers.Add(LastIndexHeader, lastIndex);

        var task = (Task<Result<ReplicationStatus>>)ParseResponseMethod.Invoke(message, [response, TestToken])!;
        return await task;
    }
}
