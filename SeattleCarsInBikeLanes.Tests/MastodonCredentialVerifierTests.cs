using System.Net;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes.Tests
{
    public class MastodonCredentialVerifierTests
    {
        [Fact]
        public async Task DeadlineCoversAStalledBodyAfterSuccessfulHeaders()
        {
            using HttpClient client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledStream())
            }));
            MastodonCredentialVerifier verifier = new MastodonCredentialVerifier(client, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
                verifier.VerifyAsync("https://example.test", "test-token", default).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task CallerCancellationDuringBodyReadIsNotCredentialRejectionOrProviderFallback()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            using HttpClient client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledStream(cancellation.Cancel))
            }));
            MastodonCredentialVerifier verifier = new MastodonCredentialVerifier(client, TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                verifier.VerifyAsync("https://example.test", "test-token", cancellation.Token));
        }

        [Fact]
        public async Task CompleteIdentityStillSucceedsWithinDeadline()
        {
            using HttpClient client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"account","username":"rider"}""")
            }));
            MastodonCredentialVerifier verifier = new MastodonCredentialVerifier(client, TimeSpan.FromSeconds(1));
            Assert.Equal(new VerifiedMastodonAccount("https://example.test", "account", "rider"),
                await verifier.VerifyAsync("https://example.test", "test-token", default));
        }

        private sealed class ResponseHandler : HttpMessageHandler
        {
            private readonly Func<HttpResponseMessage> response;

            public ResponseHandler(Func<HttpResponseMessage> response)
            {
                this.response = response;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(response());
            }
        }

        private sealed class StalledStream : Stream
        {
            private readonly Action? onRead;

            public StalledStream(Action? onRead = null)
            {
                this.onRead = onRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get
                {
                    throw new NotSupportedException();
                }
                set
                {
                    throw new NotSupportedException();
                }
            }

            public override void Flush()
            {
                throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                onRead?.Invoke();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
