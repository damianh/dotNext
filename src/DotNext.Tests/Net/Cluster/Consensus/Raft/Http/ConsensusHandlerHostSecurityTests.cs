using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

/// <summary>
/// Contract checks for issue #23: the host owns protection of the consensus endpoint.
/// </summary>
/// <remarks>
/// The published guide (https://dotnet.github.io/dotNext/features/cluster/raft.html) says:
/// "UseConsensusProtocolHandler method should be called before registration of any authentication/authorization middleware".
/// <see cref="ConfigurationExtensions.UseConsensusProtocolHandler(Microsoft.AspNetCore.Builder.IApplicationBuilder)"/>
/// maps a terminal branch, so middleware registered after it never runs for consensus requests.
/// The authentication scheme below stands for any host-owned mechanism; the library does not mandate one.
/// </remarks>
[Collection(TestCollections.Raft)]
public sealed class ConsensusHandlerHostSecurityTests : RaftTest
{
    private const int Port = 3262;
    private const string ProtocolPath = "/cluster-consensus/raft";
    private const string SchemeName = "TestPeer";
    private const string CredentialHeader = "X-Test-Peer-Credential";
    private const string Credential = "test-peer-credential";

    // The published ordering: the consensus handler first, authentication and authorization afterwards.
    // Expected: the host's fallback policy rejects an unauthenticated consensus request.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task UnauthenticatedConsensusRequestIsRejectedByHostProtection()
    {
        using var host = CreateHost<PublishedOrderingStartup>();
        await host.StartAsync(TestToken);

        using var response = await SendMetadataRequestAsync(credential: null);
        Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await host.StopAsync(TestToken);
    }

    // The sender claims an arbitrary member ID. The ID is self-asserted and is not a credential.
    private static async Task<HttpResponseMessage> SendMetadataRequestAsync(string credential)
    {
        using var client = new HttpClient { BaseAddress = new($"http://localhost:{Port}") };
        using var request = new HttpRequestMessage(HttpMethod.Post, ProtocolPath);
        request.Headers.Add("X-Raft-Message-Type", "Metadata");
        request.Headers.Add("X-Raft-Node-ID", new ClusterMemberId(Random.Shared).ToString());
        request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString("N"));
        if (credential is not null)
            request.Headers.Add(CredentialHeader, credential);

        return await client.SendAsync(request, TestToken);
    }

    private static IHost CreateHost<TStartup>()
        where TStartup : class
        => new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseKestrel(static options => options.ListenLocalhost(Port))
                .UseStartup<TStartup>())
            .ConfigureHostOptions(static options => options.ShutdownTimeout = DefaultTimeout)
            .ConfigureAppConfiguration(static builder => builder.AddInMemoryCollection(new Dictionary<string, string>
            {
                ["publicEndPoint"] = $"http://localhost:{Port}",
                ["coldStart"] = "true",
            }))
            .ConfigureLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning))
            .JoinCluster()
            .Build();

    private abstract class HostProtectionStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services
                .AddSingleton<IPersistentState>(new ConsensusOnlyState())
                .UseInMemoryConfigurationStorage()
                .AddRouting()
                .AddAuthentication(SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestPeerAuthenticationHandler>(SchemeName, configureOptions: null);

            services.AddAuthorization(static options =>
                options.FallbackPolicy = new AuthorizationPolicyBuilder(SchemeName).RequireAuthenticatedUser().Build());
        }
    }

    private sealed class PublishedOrderingStartup : HostProtectionStartup
    {
        public void Configure(IApplicationBuilder app)
        {
            app.UseConsensusProtocolHandler();
            app.UseAuthentication();
            app.UseAuthorization();
        }
    }

    private sealed class TestPeerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(CredentialHeader, out var value))
                return Task.FromResult(AuthenticateResult.NoResult());

            if (value != Credential)
                return Task.FromResult(AuthenticateResult.Fail("Invalid peer credential"));

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "cluster-peer")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new(new(identity), SchemeName)));
        }
    }
}
