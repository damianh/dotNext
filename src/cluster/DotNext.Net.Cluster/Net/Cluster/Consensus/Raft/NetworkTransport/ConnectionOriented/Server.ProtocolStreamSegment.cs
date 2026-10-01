namespace DotNext.Net.Cluster.Consensus.Raft.NetworkTransport.ConnectionOriented;

using IO;

partial class Server
{
    private class ProtocolStreamSegment(ProtocolStream protocol) : IDataTransferObject
    {
        private ProtocolStream? protocol = protocol;

        public ValueTask EnsureConsumedAsync(CancellationToken token)
        {
            ValueTask task;
            if (protocol is null)
            {
                task = ValueTask.CompletedTask;
            }
            else
            {
                task = protocol.SkipAsync(token);
                protocol = null;
            }

            return task;
        }
        
        bool IDataTransferObject.IsReusable => false;

        long? IDataTransferObject.Length => null;
        
        ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            ValueTask task;
            if (protocol is null)
            {
                task = ValueTask.CompletedTask;
            }
            else
            {
                // a derived class can declare the length of the payload
                task = CopyFromAsync(writer, protocol, ((IDataTransferObject)this).Length, token);
                protocol = null;
            }

            return task;
        }

        private static async ValueTask CopyFromAsync<TWriter>(TWriter writer, ProtocolStream protocol, long? declaredLength, CancellationToken token)
            where TWriter : IAsyncBinaryWriter
        {
            await writer.CopyFromAsync(protocol, count: null, token).ConfigureAwait(false);
            protocol.EnsurePayloadLength(declaredLength);
        }
    }
}