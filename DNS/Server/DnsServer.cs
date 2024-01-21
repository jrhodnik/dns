#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.IO;
using DNS.Protocol;
using DNS.Protocol.Utils;
using DNS.Client;
using DNS.Client.RequestResolver;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace DNS.Server {
    public class DnsServer : IDisposable {
        private const int SIO_UDP_CONNRESET = unchecked((int) 0x9800000C);
        private const int DEFAULT_PORT = 53;
        private const int UDP_TIMEOUT = 2000;

        public event EventHandler<RequestedEventArgs>? Requested;
        public event EventHandler<RespondedEventArgs>? Responded;

        private readonly IRequestResolver resolver;
        private readonly ILogger<DnsServer> logger;
        private readonly CancellationTokenSource _cts = new();

        private bool disposed = false;
        private UdpClient udp;

        public DnsServer(IPEndPoint endServer, ILogger<DnsServer> logger) :
            this(new UdpRequestResolver(endServer), logger)
        { }

        public DnsServer(IRequestResolver resolver, ILogger<DnsServer> logger, IPEndPoint? listenEndpoint = null) {
            this.resolver = resolver;
            this.logger = logger;

            if (listenEndpoint == null)
                listenEndpoint = new IPEndPoint(IPAddress.Any, DEFAULT_PORT);

            udp = new UdpClient(listenEndpoint);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], new byte[4]);
            }
        }

        public async Task Listen()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var res = await udp.ReceiveAsync(_cts.Token);
                    _ = Task.Run(() => HandleRequest(res.Buffer, res.RemoteEndPoint));
                }
            }
            catch (OperationCanceledException)
            {
                // normal
            }
        }

        public void Dispose() {
            Dispose(true);
        }

        protected virtual void OnEvent<T>(EventHandler<T>? handler, T args) {
            handler?.Invoke(this, args);
        }

        protected virtual void Dispose(bool disposing) {
            if (!disposed) {
                disposed = true;

                if (disposing)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                    udp.Dispose();
                }
            }
        }

        private async Task HandleRequest(byte[] data, IPEndPoint remote)
        {
            using var scope = logger.BeginScope("Remote EndPoint: {remote}", remote);

            Request? request = null;

            try
            {
                request = Request.FromArray(data, remote);
                OnEvent(Requested, new RequestedEventArgs(request, data, remote));

                IResponse response = await resolver.Resolve(request).ConfigureAwait(false);

                OnEvent(Responded, new RespondedEventArgs(request, response, data, remote));

                await udp
                    .SendAsync(response.ToArray(), response.Size, remote)
                    .WithCancellationTimeout(TimeSpan.FromMilliseconds(UDP_TIMEOUT)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {

                return;
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or IOException or ObjectDisposedException)
            {
                logger.LogWarning(e, "Error while handling request.");
            }
            catch (ResponseException e)
            {
                IResponse? response = e.Response;

                if (response == null)
                {
                    response = Response.FromRequest(request);
                }

                try
                {
                    await udp
                        .SendAsync(response.ToArray(), response.Size, remote)
                        .WithCancellationTimeout(TimeSpan.FromMilliseconds(UDP_TIMEOUT)).ConfigureAwait(false);
                }
                catch (SocketException) { }
                catch (OperationCanceledException) { }
                finally
                {
                    logger.LogWarning(e, "Error while handling request.");
                }
            }
        }

        public class RequestedEventArgs : EventArgs {
            public RequestedEventArgs(IRequest request, byte[] data, IPEndPoint remote) {
                Request = request;
                Data = data;
                Remote = remote;
            }

            public IRequest Request { get; }
            public byte[] Data { get; }
            public IPEndPoint Remote { get; }
        }

        public class RespondedEventArgs : EventArgs {
            public RespondedEventArgs(IRequest request, IResponse response, byte[] data, IPEndPoint remote) {
                Request = request;
                Response = response;
                Data = data;
                Remote = remote;
            }

            public IRequest Request { get; }
            public IResponse Response { get; }
            public byte[] Data { get; }
            public IPEndPoint Remote { get; }
        }

        public class ErroredEventArgs : EventArgs {
            public ErroredEventArgs(Exception e) {
                Exception = e;
            }

            public Exception Exception { get; }
        }

        private class FallbackRequestResolver : IRequestResolver {
            private IRequestResolver[] resolvers;

            public FallbackRequestResolver(params IRequestResolver[] resolvers) {
                this.resolvers = resolvers;
            }

            public async Task<IResponse?> Resolve(IRequest request, CancellationToken cancellationToken = default(CancellationToken)) {
                IResponse? response = null;

                foreach (IRequestResolver resolver in resolvers) {
                    response = await resolver.Resolve(request, cancellationToken).ConfigureAwait(false);
                    if (response.AnswerRecords.Count > 0) break;
                }

                return response;
            }
        }
    }
}
