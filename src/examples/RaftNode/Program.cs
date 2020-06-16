using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using static DotNext.Threading.AsyncEvent;

namespace RaftNode
{
    public static class Program
    {
        private static async Task Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Port number not specified");
            }
            else
            {
                var port = int.Parse(args[0]);
                var configuration = new RaftCluster.TcpConfiguration(new IPEndPoint(IPAddress.Loopback, port))
                {
                    LowerElectionTimeout = 150,
                    UpperElectionTimeout = 300,
                    TransmissionBlockSize = 4096
                };

                await UseConfiguration(configuration, args[1]);
            }
        }

        private static async Task UseConfiguration(RaftCluster.NodeConfiguration config, string? persistentStorage)
        {
            config.Members.Add(new IPEndPoint(IPAddress.Loopback, 3262));
            config.Members.Add(new IPEndPoint(IPAddress.Loopback, 3263));
            config.Members.Add(new IPEndPoint(IPAddress.Loopback, 3264));
            var loggerFactory = new LoggerFactory();
            var loggerOptions = new ConsoleLoggerOptions
            {
                LogToStandardErrorThreshold = LogLevel.Warning
            };
            loggerFactory.AddProvider(
                new ConsoleLoggerProvider(new FakeOptionsMonitor<ConsoleLoggerOptions>(loggerOptions)));
            config.LoggerFactory = loggerFactory;

            using var cluster = new RaftCluster(config);
            cluster.LeaderChanged += ClusterConfigurator.LeaderChanged;
            var modifier = default(DataModifier?);
            if (!string.IsNullOrEmpty(persistentStorage))
            {
                var state = new SimplePersistentState(persistentStorage);
                cluster.AuditTrail = state;
                modifier = new DataModifier(cluster, state);
            }

            await cluster.StartAsync(CancellationToken.None);
            await (modifier?.StartAsync(CancellationToken.None) ?? Task.CompletedTask);
            using var handler = new CancelKeyPressHandler();
            Console.CancelKeyPress += handler.Handler;
            await handler.WaitAsync();
            Console.CancelKeyPress -= handler.Handler;
            await (modifier?.StopAsync(CancellationToken.None) ?? Task.CompletedTask);
            await cluster.StopAsync(CancellationToken.None);
        }
    }
}