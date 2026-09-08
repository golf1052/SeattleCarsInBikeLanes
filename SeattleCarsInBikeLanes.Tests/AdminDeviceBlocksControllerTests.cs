using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using idunno.Authentication.Basic;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SeattleCarsInBikeLanes.Controllers;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Database.Models;
using SeattleCarsInBikeLanes.Models;

namespace SeattleCarsInBikeLanes.Tests
{
    public class AdminDeviceBlocksControllerTests
    {
        private const string Route = "/api/AdminPage/BlockedDevices";
        private const string DeviceId = "Case-Preserved_123";
        private const string Reason = "<b>Administrator-only plain text</b>";
        private const string SocialScheme = "TestSocial";

        [Fact]
        public async Task GetReturnsOnlyAdminDtosAndForwardsCancellation()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            database.Setup(d => d.ListAsync(cancellation.Token)).ReturnsAsync(new[]
            {
                new BlockedDevice { DeviceId = DeviceId, Reason = Reason },
                new BlockedDevice { DeviceId = "other", Reason = "Another reason" }
            });
            AdminDeviceBlocksController controller = CreateController(database);

            OkObjectResult result = Assert.IsType<OkObjectResult>(await controller.Get(cancellation.Token));

            var devices = Assert.IsType<AdminBlockedDeviceResponse[]>(result.Value);
            Assert.Equal(new[] { new AdminBlockedDeviceResponse(DeviceId, Reason), new AdminBlockedDeviceResponse("other", "Another reason") }, devices);
            database.VerifyAll();
            database.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(128, 1000)]
        public async Task MutationsNormalizeWithoutChangingCaseAndAcceptExactBoundaries(int idLength, int reasonLength)
        {
            string id = new string('A', idLength);
            string reason = new string('r', reasonLength);
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            database.Setup(d => d.BlockAsync(id, reason, cancellation.Token)).Returns(Task.CompletedTask);
            database.Setup(d => d.UnblockAsync(id, cancellation.Token)).Returns(Task.CompletedTask);
            AdminDeviceBlocksController controller = CreateController(database);

            Assert.IsType<NoContentResult>(await controller.Block(
                new BlockDeviceRequest { DeviceId = $" \t{id}\n ", Reason = $" \n{reason}\t " }, cancellation.Token));
            Assert.IsType<NoContentResult>(await controller.Unblock(
                new UnblockDeviceRequest { DeviceId = $" \t{id}\n " }, cancellation.Token));

            database.VerifyAll();
            database.VerifyNoOtherCalls();
        }

        [Theory]
        [MemberData(nameof(BlockedDevicesDatabaseTests.InvalidDeviceIds), MemberType = typeof(BlockedDevicesDatabaseTests))]
        public async Task InvalidIdsReturn400WithoutStorageAccess(string? deviceId)
        {
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            AdminDeviceBlocksController controller = CreateController(database);

            Assert.IsType<BadRequestObjectResult>(await controller.Block(new BlockDeviceRequest { DeviceId = deviceId, Reason = Reason }));
            Assert.IsType<BadRequestObjectResult>(await controller.Unblock(new UnblockDeviceRequest { DeviceId = deviceId }));

            database.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \r\n\t")]
        public async Task MissingReasonReturns400WithoutStorageAccess(string? reason)
        {
            Mock<BlockedDevicesDatabase> database = CreateDatabase();

            Assert.IsType<BadRequestObjectResult>(await CreateController(database)
                .Block(new BlockDeviceRequest { DeviceId = DeviceId, Reason = reason }));

            database.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task OverlongTrimmedReasonReturns400WithoutStorageAccess()
        {
            Mock<BlockedDevicesDatabase> database = CreateDatabase();

            Assert.IsType<BadRequestObjectResult>(await CreateController(database)
                .Block(new BlockDeviceRequest { DeviceId = DeviceId, Reason = " " + new string('x', 1001) + " " }));

            database.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("get")]
        [InlineData("block")]
        [InlineData("unblock")]
        public async Task ExpectedStorageFailuresAreLoggedAndReturnHonest503WithoutExceptionDetails(string operation)
        {
            foreach (Exception failure in new Exception[]
            {
                new CosmosException(Reason, HttpStatusCode.NotFound, 0, "test", 0),
                new CosmosException(Reason, HttpStatusCode.Unauthorized, 0, "test", 0),
                new CosmosException(Reason, HttpStatusCode.Forbidden, 0, "test", 0),
                new CosmosException(Reason, HttpStatusCode.TooManyRequests, 0, "test", 0),
                new CosmosException(Reason, HttpStatusCode.ServiceUnavailable, 0, "test", 0),
                new HttpRequestException(Reason), new TimeoutException(Reason),
                new Azure.Identity.AuthenticationFailedException(Reason), new Newtonsoft.Json.JsonException(Reason),
                new OperationCanceledException(Reason)
            })
            {
                Mock<BlockedDevicesDatabase> database = CreateDatabase();
                SetupFailure(database, operation, failure, CancellationToken.None);
                Mock<ILogger<AdminDeviceBlocksController>> logger = new Mock<ILogger<AdminDeviceBlocksController>>();
                AdminDeviceBlocksController controller = CreateController(database, logger.Object);

                ObjectResult result = Assert.IsType<ObjectResult>(await Invoke(controller, operation));

                Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
                string message = Assert.IsType<string>(result.Value);
                Assert.Contains("Refresh", message);
                Assert.DoesNotContain(Reason, message);
                Assert.DoesNotContain(DeviceId, message);
                VerifyLoggedFailure(logger, failure);
                database.VerifyAll();
            }
        }

        [Theory]
        [InlineData("get")]
        [InlineData("block")]
        [InlineData("unblock")]
        public async Task UnexpectedErrorsPropagateWithoutBeingReportedAsStorageOutage(string operation)
        {
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            InvalidOperationException failure = new InvalidOperationException("Programming failure");
            SetupFailure(database, operation, failure, CancellationToken.None);
            Mock<ILogger<AdminDeviceBlocksController>> logger = new Mock<ILogger<AdminDeviceBlocksController>>();

            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Invoke(CreateController(database, logger.Object), operation)));

            logger.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("get")]
        [InlineData("block")]
        [InlineData("unblock")]
        public async Task CallerCancellationBeforeAndDuringStoragePropagatesWithoutLoggingAnOutage(string operation)
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            Mock<ILogger<AdminDeviceBlocksController>> logger = new Mock<ILogger<AdminDeviceBlocksController>>();
            AdminDeviceBlocksController controller = CreateController(database, logger.Object);
            OperationCanceledException failure = new OperationCanceledException(cancellation.Token);
            SetupFailure(database, operation, failure, cancellation.Token, cancellation.Cancel);

            Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() =>
                Invoke(controller, operation, cancellation.Token)));

            database.VerifyAll();
            database.Invocations.Clear();
            var preCanceled = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                Invoke(controller, operation, cancellation.Token));
            Assert.Equal(cancellation.Token, preCanceled.CancellationToken);
            database.VerifyNoOtherCalls();
            logger.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("get")]
        [InlineData("block")]
        [InlineData("unblock")]
        public async Task CallerCancellationTakesPriorityOverConcurrentStorageFailures(string operation)
        {
            foreach (Exception failure in new Exception[]
            {
                new CosmosException("Unavailable", HttpStatusCode.ServiceUnavailable, 0, "test", 0),
                new HttpRequestException("Offline"), new TimeoutException("Timeout"),
                new Azure.Identity.AuthenticationFailedException("Unavailable credentials"),
                new Newtonsoft.Json.JsonException("Invalid stored document")
            })
            {
                using CancellationTokenSource cancellation = new CancellationTokenSource();
                Mock<BlockedDevicesDatabase> database = CreateDatabase();
                Mock<ILogger<AdminDeviceBlocksController>> logger = new Mock<ILogger<AdminDeviceBlocksController>>();
                SetupFailure(database, operation, failure, cancellation.Token, cancellation.Cancel);

                var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    Invoke(CreateController(database, logger.Object), operation, cancellation.Token));

                Assert.Equal(cancellation.Token, error.CancellationToken);
                logger.VerifyNoOtherCalls();
                database.VerifyAll();
            }
        }

        [Fact]
        public async Task HttpRoutesRequireActualBasicAuthenticationNotDefaultSocialOrMobileCredentials()
        {
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            await using ApiHost host = await ApiHost.Start(database);
            foreach (string credential in new[] { "none", "wrong-basic", "bearer", "cookie", "wrong-basic-with-cookie" })
            {
                if (credential is "bearer" or "cookie")
                {
                    using HttpRequestMessage socialRequest = new HttpRequestMessage(HttpMethod.Get, "/test/social-identity");
                    SetNonAdminCredential(socialRequest, credential);
                    using HttpResponseMessage socialResponse = await host.Client.SendAsync(socialRequest);
                    Assert.Equal(HttpStatusCode.OK, socialResponse.StatusCode);
                    Assert.Equal("true", await socialResponse.Content.ReadAsStringAsync());
                }

                foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
                {
                    using HttpRequestMessage request = Request(method, """{"deviceId":"Case-Preserved_123","reason":"private"}""", authenticated: false);
                    SetNonAdminCredential(request, credential);
                    using HttpResponseMessage response = await host.Client.SendAsync(request);

                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                    Assert.Contains(response.Headers.WwwAuthenticate, challenge => challenge.Scheme == "Basic");
                    Assert.DoesNotContain(Reason, await response.Content.ReadAsStringAsync());
                }
            }

            database.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task HttpRoutesUseJsonAdminDtoNoStoreAndIdempotentMutations()
        {
            Dictionary<string, string> items = new Dictionary<string, string>(StringComparer.Ordinal);
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            database.Setup(d => d.BlockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((id, reason, token) =>
                {
                    Assert.True(token.CanBeCanceled);
                    items[id] = reason;
                })
                .Returns(Task.CompletedTask);
            database.Setup(d => d.UnblockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((id, token) =>
                {
                    Assert.True(token.CanBeCanceled);
                    items.Remove(id);
                })
                .Returns(Task.CompletedTask);
            database.Setup(d => d.ListAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken token) =>
                {
                    Assert.True(token.CanBeCanceled);
                    return Task.FromResult<IReadOnlyList<BlockedDevice>>(items.Select(item =>
                        new BlockedDevice { DeviceId = item.Key, Reason = item.Value }).ToArray());
                });
            await using ApiHost host = await ApiHost.Start(database);
            string body = JsonSerializer.Serialize(new { deviceId = $" \t{DeviceId}\n ", reason = $" \n{Reason}\t " });
            for (int retry = 0; retry < 2; retry++)
            {
                using HttpRequestMessage blockRequest = Request(HttpMethod.Post, body);
                using HttpResponseMessage blockResponse = await host.Client.SendAsync(blockRequest);
                Assert.Equal(HttpStatusCode.NoContent, blockResponse.StatusCode);
                Assert.Equal("", await blockResponse.Content.ReadAsStringAsync());
            }

            using (HttpRequestMessage listRequest = Request(HttpMethod.Get))
            using (HttpResponseMessage listResponse = await host.Client.SendAsync(listRequest))
            {
                Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
                Assert.True(listResponse.Headers.CacheControl?.NoStore);
                using JsonDocument json = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
                JsonElement item = Assert.Single(json.RootElement.EnumerateArray());
                Assert.Equal(new[] { "deviceId", "reason" }, item.EnumerateObject().Select(property => property.Name));
                Assert.Equal(DeviceId, item.GetProperty("deviceId").GetString());
                Assert.Equal(Reason, item.GetProperty("reason").GetString());
            }

            for (int retry = 0; retry < 2; retry++)
            {
                using HttpRequestMessage unblockRequest = Request(HttpMethod.Delete, JsonSerializer.Serialize(new { deviceId = $" {DeviceId} " }));
                using HttpResponseMessage unblockResponse = await host.Client.SendAsync(unblockRequest);
                Assert.Equal(HttpStatusCode.NoContent, unblockResponse.StatusCode);
            }
            using HttpRequestMessage emptyListRequest = Request(HttpMethod.Get);
            using HttpResponseMessage emptyListResponse = await host.Client.SendAsync(emptyListRequest);
            Assert.Equal(HttpStatusCode.OK, emptyListResponse.StatusCode);
            Assert.Equal("[]", await emptyListResponse.Content.ReadAsStringAsync());
            database.Verify(d => d.BlockAsync(DeviceId, Reason, It.IsAny<CancellationToken>()), Times.Exactly(2));
            database.Verify(d => d.UnblockAsync(DeviceId, It.IsAny<CancellationToken>()), Times.Exactly(2));
            database.Verify(d => d.ListAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
            database.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task HttpMutationBindingRejectsInvalidJsonIdsAndReasonsBeforeStorage()
        {
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            await using ApiHost host = await ApiHost.Start(database);
            List<string> commonInvalidBodies = new List<string>
            {
                "", "null", "[]", "{", "{}", """{"deviceId":null}""", """{"deviceId":42}""",
                """{"deviceId":""}""", """{"deviceId":" \t"}""", """{"deviceId":"a/b"}""",
                """{"deviceId":"a b"}""", """{"deviceId":"é"}""",
                JsonSerializer.Serialize(new { deviceId = new string('a', 129), reason = Reason })
            };
            foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Delete })
            {
                foreach (string body in commonInvalidBodies)
                {
                    using HttpRequestMessage request = Request(method, body);
                    using HttpResponseMessage response = await host.Client.SendAsync(request);
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                }
            }
            foreach (string body in new[]
            {
                """{"deviceId":"valid"}""", """{"deviceId":"valid","reason":null}""",
                """{"deviceId":"valid","reason":""}""", """{"deviceId":"valid","reason":" \t\n"}""",
                """{"deviceId":"valid","reason":42}""",
                JsonSerializer.Serialize(new { deviceId = "valid", reason = " " + new string('r', 1001) + " " })
            })
            {
                using HttpRequestMessage request = Request(HttpMethod.Post, body);
                using HttpResponseMessage response = await host.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            database.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task HttpMutationsRejectNonJsonAndDoNotHaveGetOrLegacyAliases()
        {
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            await using ApiHost host = await ApiHost.Start(database);
            foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Delete })
            {
                foreach (string contentType in new[] { "text/plain", "application/x-www-form-urlencoded", "text/json" })
                {
                    using HttpRequestMessage request = Request(method, """{"deviceId":"valid","reason":"reason"}""");
                    request.Content!.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    using HttpResponseMessage response = await host.Client.SendAsync(request);
                    Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
                }
            }
            using (HttpRequestMessage put = Request(HttpMethod.Put, """{"deviceId":"valid","reason":"reason"}"""))
            using (HttpResponseMessage response = await host.Client.SendAsync(put))
            {
                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            }
            foreach (string path in new[] { Route + "/Block", Route + "/Unblock", "/api/AdminDeviceBlocks" })
            {
                using HttpRequestMessage request = Request(HttpMethod.Get);
                request.RequestUri = new Uri(path, UriKind.Relative);
                using HttpResponseMessage response = await host.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            database.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task HttpFailuresReturn503RatherThanSuccessOrAnEmptyList()
        {
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            CosmosException failure = new CosmosException(Reason, HttpStatusCode.Forbidden, 0, "test", 0);
            database.Setup(d => d.ListAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            database.Setup(d => d.BlockAsync(DeviceId, Reason, It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            database.Setup(d => d.UnblockAsync(DeviceId, It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            await using ApiHost host = await ApiHost.Start(database);
            foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
            {
                using HttpRequestMessage request = Request(method, JsonSerializer.Serialize(new { deviceId = DeviceId, reason = Reason }));
                using HttpResponseMessage response = await host.Client.SendAsync(request);

                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                string message = await response.Content.ReadAsStringAsync();
                Assert.Contains("Refresh", message);
                Assert.DoesNotContain(Reason, message);
                Assert.NotEqual("[]", message);
                if (method == HttpMethod.Get)
                {
                    Assert.True(response.Headers.CacheControl?.NoStore);
                }
            }
            database.VerifyAll();
        }

        [Fact]
        public async Task HttpRequestCancellationReachesTheDatabase()
        {
            TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Mock<BlockedDevicesDatabase> database = CreateDatabase();
            database.Setup(d => d.ListAsync(It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken token) =>
                {
                    started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return (IReadOnlyList<BlockedDevice>)Array.Empty<BlockedDevice>();
                    }
                    finally
                    {
                        canceled.TrySetResult(token.IsCancellationRequested);
                    }
                });
            await using ApiHost host = await ApiHost.Start(database);
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            using HttpRequestMessage request = Request(HttpMethod.Get);
            Task<HttpResponseMessage> response = host.Client.SendAsync(request, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
            Assert.True(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            database.Verify(d => d.ListAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        private static Mock<BlockedDevicesDatabase> CreateDatabase() =>
            new Mock<BlockedDevicesDatabase>(MockBehavior.Strict, NullLogger<BlockedDevicesDatabase>.Instance, Mock.Of<Container>());

        private static AdminDeviceBlocksController CreateController(Mock<BlockedDevicesDatabase> database,
            ILogger<AdminDeviceBlocksController>? logger = null) =>
            new AdminDeviceBlocksController(logger ?? NullLogger<AdminDeviceBlocksController>.Instance, database.Object);

        private static Task<IActionResult> Invoke(AdminDeviceBlocksController controller, string operation, CancellationToken token = default) =>
            operation switch
            {
                "get" => controller.Get(token),
                "block" => controller.Block(new BlockDeviceRequest { DeviceId = DeviceId, Reason = Reason }, token),
                "unblock" => controller.Unblock(new UnblockDeviceRequest { DeviceId = DeviceId }, token),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };

        private static void SetupFailure(Mock<BlockedDevicesDatabase> database, string operation, Exception failure,
            CancellationToken token, Action? beforeFailure = null)
        {
            Action callback = beforeFailure ?? (() => { });
            switch (operation)
            {
                case "get":
                    database.Setup(d => d.ListAsync(token)).Callback(callback).ThrowsAsync(failure);
                    break;
                case "block":
                    database.Setup(d => d.BlockAsync(DeviceId, Reason, token)).Callback(callback).ThrowsAsync(failure);
                    break;
                case "unblock":
                    database.Setup(d => d.UnblockAsync(DeviceId, token)).Callback(callback).ThrowsAsync(failure);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private static void VerifyLoggedFailure(Mock<ILogger<AdminDeviceBlocksController>> logger, Exception failure)
        {
            logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), failure,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }

        private static HttpRequestMessage Request(HttpMethod method, string? body = null, bool authenticated = true)
        {
            HttpRequestMessage request = new HttpRequestMessage(method, Route);
            if (body is not null && method != HttpMethod.Get)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("test-admin:test-password")));
            }
            return request;
        }

        private static void SetNonAdminCredential(HttpRequestMessage request, string credential)
        {
            if (credential is "wrong-basic" or "wrong-basic-with-cookie")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("test-admin:incorrect")));
            }
            if (credential == "bearer")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "social-token");
            }
            if (credential is "cookie" or "wrong-basic-with-cookie")
            {
                request.Headers.Add("Cookie", "test-social=session");
            }
        }

        private sealed class ApiHost : IAsyncDisposable
        {
            private readonly WebApplication app;
            public HttpClient Client { get; }

            private ApiHost(WebApplication app)
            {
                this.app = app;
                Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            }

            public static async Task<ApiHost> Start(Mock<BlockedDevicesDatabase> database)
            {
                WebApplicationBuilder builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                builder.Services.AddControllers().AddApplicationPart(typeof(AdminDeviceBlocksController).Assembly);
                builder.Services.AddSingleton(database.Object);
                // Deliberately use a non-admin default to prove the controller requires Basic explicitly.
                builder.Services.AddAuthentication(SocialScheme)
                    .AddScheme<AuthenticationSchemeOptions, SocialAuthenticationHandler>(SocialScheme, _ => { })
                    .AddBasic(options =>
                    {
                        options.Realm = "Admin page";
                        options.AllowInsecureProtocol = true;
                        options.Events = new BasicAuthenticationEvents
                        {
                            OnValidateCredentials = context =>
                            {
                                if (context.Username == "test-admin" && context.Password == "test-password")
                                {
                                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                                        new[] { new Claim(ClaimTypes.NameIdentifier, context.Username) },
                                        BasicAuthenticationDefaults.AuthenticationScheme));
                                    context.Success();
                                }
                                return Task.CompletedTask;
                            }
                        };
                    });
                builder.Services.AddAuthorization();
                WebApplication app = builder.Build();
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapControllers();
                app.MapGet("/test/social-identity", (HttpContext context) => Results.Ok(context.User.Identity!.IsAuthenticated))
                    .RequireAuthorization();
                await app.StartAsync();
                return new ApiHost(app);
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await app.DisposeAsync();
            }
        }

        public sealed class SocialAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public SocialAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder)
            {
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (Request.Headers.Authorization == "Bearer social-token" || Request.Cookies["test-social"] == "session")
                {
                    ClaimsPrincipal principal = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "social-user") }, SocialScheme));
                    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SocialScheme)));
                }
                return Task.FromResult(AuthenticateResult.NoResult());
            }
        }
    }
}
