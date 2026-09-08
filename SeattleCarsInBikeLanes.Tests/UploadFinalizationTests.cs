using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Azure;
using Azure.AI.ContentSafety;
using Azure.AI.Vision.ImageAnalysis;
using Azure.Core.Pipeline;
using Azure.Maps.Search;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ImageMagick;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using SeattleCarsInBikeLanes.Controllers;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;
using Cosmos = Microsoft.Azure.Cosmos;

namespace SeattleCarsInBikeLanes.Tests
{
    public partial class UploadFinalizationTests
    {
        private const string Id = "0123456789abcdef0123456789abcdef";
        private const string Did = "did:plc:verified";

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public async Task AnonymousUsesOrderedCanonicalPhotosAndStripsUntrustedMetadata(int count)
        {
            using Fixture fixture = new Fixture();
            var request = fixture.Prepare(count);
            foreach (var photo in request.Photos)
            {
                photo.Attribute = true;
                photo.TwitterAccessToken = photo.MastodonAccessToken = photo.ThreadsAccessToken = "secret";
                photo.TwitterUsername = photo.MastodonUsername = photo.ThreadsUsername = "forged";
                photo.MastodonEndpoint = "https://forged.test";
                photo.TwitterSubmittedBy = photo.MastodonSubmittedBy = photo.BlueskySubmittedBy =
                    photo.ThreadsSubmittedBy = "Submitted by forged";
                photo.Tags = [new ImageTag() { Name = "forged", Confidence = 1 }];
            }
            fixture.Controller.Request.Headers.Authorization = "Bearer invalid";
            fixture.Cookie = fixture.Bearer = Fixture.Auth(Did, "ambient.test");

            var result = Assert.IsType<OkObjectResult>(await fixture.Controller.FinalizeUpload(request));

            Assert.Equal(fixture.Receipt, Assert.IsType<SubmissionReceipt>(result.Value));
            Assert.NotNull(fixture.Committed);
            Assert.Equal(count, fixture.Committed.Count);
            for (int i = 0; i < count; i++)
            {
                var source = fixture.Committed[i];
                var metadata = source.Metadata;
                Assert.Equal($"initialupload/photo-{i}.jpeg", source.BlobName);
                Assert.Equal("\"photo-version\"", source.ETag);
                Assert.Equal(23, source.Length);
                Assert.Equal($"{Id}-{i}", metadata.PhotoId);
                Assert.Equal(Id, metadata.SubmissionId);
                Assert.Equal(Id, metadata.ReportId);
                Assert.Equal(i, metadata.PhotoNumber);
                Assert.Equal("car", Assert.Single(metadata.Tags).Name);
                Assert.False(metadata.Attribute);
                Assert.Null(metadata.BlueskyHandle);
                Assert.Null(metadata.BlueskyUserDid);
                Assert.Null(metadata.MastodonEndpoint);
                Assert.Null(metadata.MastodonFullUsername);
                Assert.Null(metadata.TwitterUsername);
                Assert.Null(metadata.ThreadsUsername);
                Assert.Null(metadata.TwitterAccessToken);
                Assert.Null(metadata.MastodonAccessToken);
                Assert.Null(metadata.ThreadsAccessToken);
                Assert.Null(metadata.BlueskyAdminDid);
                Assert.Null(metadata.BlueskyAccessJwt);
                Assert.Null(metadata.TwitterLink);
                Assert.Equal("Submission", metadata.BlueskySubmittedBy);
                Assert.Equal("Submission", metadata.MastodonSubmittedBy);
            }
            fixture.Authentication.Verify(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>()), Times.Never);
            fixture.Provider.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BlueskyUsesOnlyTheSelectedSchemeAndVerifiedHandle(bool bearer)
        {
            using Fixture fixture = new Fixture();
            var request = fixture.Prepare() with
            {
                Attribution = new ReportAttribution(BlueskyDid: Did)
            };
            fixture.Cookie = Fixture.Auth(bearer ? "did:plc:other" : Did, "cookie.test");
            fixture.Bearer = Fixture.Auth(Did, "native.test");
            if (bearer)
            {
                fixture.Controller.Request.Headers.Authorization = "Bearer native";
            }

            Assert.IsType<OkObjectResult>(await fixture.Controller.FinalizeUpload(request));

            var photo = Assert.Single(fixture.Committed!).Metadata;
            Assert.Equal(Did, photo.BlueskyUserDid);
            Assert.Equal(bearer ? "native.test" : "cookie.test", photo.BlueskyHandle);
            Assert.Equal($"Submitted by @{photo.BlueskyHandle}", photo.BlueskySubmittedBy);
            fixture.Authentication.Verify(a => a.AuthenticateAsync(It.IsAny<HttpContext>(),
                bearer ? BlueskyAuthDefaults.BearerScheme : BlueskyAuthDefaults.CookieScheme), Times.Once);
            fixture.Authentication.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("Bearer expired")]
        [InlineData("bearer expired")]
        [InlineData("Bearer")]
        [InlineData("Bearer\texpired")]
        public async Task RejectedExplicitBearerNeverFallsBackToValidCookie(string header)
        {
            using Fixture fixture = new Fixture();
            fixture.Cookie = Fixture.Auth(Did, "cookie.test");
            fixture.Bearer = AuthenticateResult.Fail("expired");
            fixture.Controller.Request.Headers.Authorization = header;

            var result = await fixture.Controller.FinalizeUpload(fixture.Prepare() with
            {
                Attribution = new ReportAttribution(BlueskyDid: Did)
            });

            AssertError(result, 401, UploadErrors.CredentialRejected);
            fixture.Authentication.Verify(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), BlueskyAuthDefaults.CookieScheme), Times.Never);
            Assert.Null(fixture.Committed);
        }

        [Fact]
        public async Task BlueskyIdentityMismatchDoesNotSilentlySwitchAccounts()
        {
            using Fixture fixture = new Fixture();
            fixture.Cookie = Fixture.Auth("did:plc:other", "other.test");
            var result = await fixture.Controller.FinalizeUpload(fixture.Prepare() with
            {
                Attribution = new ReportAttribution(BlueskyDid: Did)
            });
            AssertError(result, 409, UploadErrors.IdentityMismatch);
            Assert.Null(fixture.Committed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MastodonAndCombinedAttributionUseCanonicalVerifiedIdentity(bool combined)
        {
            using Fixture fixture = new Fixture();
            fixture.Cookie = Fixture.Auth(Did, "verified.test");
            fixture.MastodonResponse(HttpStatusCode.OK, """{"id":"42","username":"actual"}""");
            var request = fixture.Prepare(2) with
            {
                Attribution = new ReportAttribution(combined ? Did : null, "https://MASTODON.example:443/", "42")
            };
            foreach (var photo in request.Photos)
            {
                photo.MastodonAccessToken = "retained-token";
            }

            Assert.IsType<OkObjectResult>(await fixture.Controller.FinalizeUpload(request));

            Assert.Equal("https://mastodon.example", fixture.CommittedAttribution!.MastodonServer);
            foreach (var photo in fixture.Committed!)
            {
                Assert.Equal("@actual@mastodon.example", photo.Metadata.MastodonFullUsername);
                Assert.Equal("actual", photo.Metadata.MastodonUsername);
                Assert.Null(photo.Metadata.MastodonAccessToken);
                Assert.Equal(combined ? Did : null, photo.Metadata.BlueskyUserDid);
            }
        }

        [Theory]
        [InlineData(401, 401, UploadErrors.CredentialRejected)]
        [InlineData(403, 401, UploadErrors.CredentialRejected)]
        [InlineData(429, 503, UploadErrors.ProviderUnavailable)]
        [InlineData(503, 503, UploadErrors.ProviderUnavailable)]
        [InlineData(200, 409, UploadErrors.IdentityMismatch)]
        public async Task MastodonFailuresNeverFallBackToAnonymous(int providerStatus, int status, string code)
        {
            using Fixture fixture = new Fixture();
            fixture.MastodonResponse((HttpStatusCode)providerStatus, """{"id":"other","username":"someone"}""");
            var request = fixture.Prepare() with
            {
                Attribution = new ReportAttribution(null, "https://mastodon.example", "42")
            };
            request.Photos[0].MastodonAccessToken = "retained-token";

            AssertError(await fixture.Controller.FinalizeUpload(request), status, code);
            Assert.Null(fixture.Committed);
        }

        [Fact]
        public async Task ProviderTimeoutIsRetryableButRequestCancellationPropagates()
        {
            using Fixture fixture = new Fixture();
            fixture.Provider.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new TaskCanceledException());
            var request = fixture.Prepare() with
            {
                Attribution = new ReportAttribution(null, "https://mastodon.example", "42")
            };
            request.Photos[0].MastodonAccessToken = "retained-token";
            AssertError(await fixture.Controller.FinalizeUpload(request), 503, UploadErrors.ProviderUnavailable);

            using CancellationTokenSource cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Controller.FinalizeUpload(request, cancellation.Token));
            Assert.Null(fixture.Committed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AcceptedReceiptWinsBeforeCredentialsAndInvalidFreshPreparation(bool retired)
        {
            using Fixture fixture = new Fixture();
            var acceptedReceipt = fixture.Receipt with
            {
                Attribution = new ReportAttribution(Did)
            };
            fixture.Existing = new SubmissionReport(acceptedReceipt, null, [], Retired: retired);
            fixture.Controller.Request.Headers.Authorization = "Bearer expired";
            var result = Assert.IsType<OkObjectResult>(await fixture.Controller.FinalizeUpload(
                new FinalizeReportRequest([], new ReportAttribution())));
            Assert.Equal(acceptedReceipt, result.Value);
            fixture.Authentication.VerifyNoOtherCalls();
            fixture.Blobs.VerifyNoOtherCalls();
            Assert.Null(fixture.Committed);
        }

        [Fact]
        public async Task ReceiptLookupIsMinimalAndAllowsAnonymousWebsiteContext()
        {
            using Fixture fixture = new Fixture();
            fixture.Existing = new SubmissionReport(fixture.Receipt, null, []);
            var result = Assert.IsType<OkObjectResult>(await fixture.Controller.ReportStatus(Id));
            Assert.Same(fixture.Receipt, result.Value);
            Assert.Null(fixture.LastDeviceId);
            fixture.Authentication.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task AbsentReceiptIs404ButUnknownStorageFailureIs503()
        {
            using Fixture fixture = new Fixture();
            Assert.IsType<NotFoundResult>(await fixture.Controller.ReportStatus(Id));
            fixture.Store.Setup(s => s.GetAsync(Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(503, "sensitive provider message"));
            AssertError(await fixture.Controller.ReportStatus(Id), 503, UploadErrors.ProviderUnavailable);
            AssertError(await fixture.Controller.FinalizeUpload(fixture.Prepare()), 503, UploadErrors.ProviderUnavailable);
        }

        [Fact]
        public async Task AStorageContainer404IsNotAnExpiredPreparation()
        {
            using Fixture fixture = new Fixture();
            var request = fixture.Prepare();
            fixture.Blobs.Setup(b => b.GetBlobClient("initialupload/photo-0.json"))
                .Throws(new RequestFailedException(404, "private detail", "ContainerNotFound", null));
            AssertError(await fixture.Controller.FinalizeUpload(request), 503, UploadErrors.ProviderUnavailable);
        }

        [Fact]
        public async Task CommitStorageFailureDoesNotPretendToBeMissingOrAccepted()
        {
            using Fixture fixture = new Fixture();
            fixture.Store.Setup(s => s.CommitAsync(Id, It.IsAny<string?>(), It.IsAny<ReportAttribution>(),
                    It.IsAny<IReadOnlyList<PreparedSubmissionPhoto>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(404, "private detail", "ContainerNotFound", null));
            AssertError(await fixture.Controller.FinalizeUpload(fixture.Prepare()), 503, UploadErrors.ProviderUnavailable);
        }

        [Fact]
        public async Task PreparationExpiringDuringCommitReturnsTypedGone()
        {
            using Fixture fixture = new Fixture();
            fixture.Store.Setup(s => s.CommitAsync(Id, It.IsAny<string?>(), It.IsAny<ReportAttribution>(),
                    It.IsAny<IReadOnlyList<PreparedSubmissionPhoto>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PreparationExpiredException());
            AssertError(await fixture.Controller.FinalizeUpload(fixture.Prepare()), 410, UploadErrors.PreparationExpired);
        }

        [Fact]
        public async Task ReceiptContextMismatchIsForbiddenBeforeAuthentication()
        {
            using Fixture fixture = new Fixture();
            fixture.Store.Setup(s => s.GetAsync(Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new UnauthorizedAccessException());
            Assert.Equal(403, Assert.IsType<StatusCodeResult>(await fixture.Controller.ReportStatus(Id)).StatusCode);
            Assert.Equal(403, Assert.IsType<StatusCodeResult>(
                await fixture.Controller.FinalizeUpload(fixture.Prepare())).StatusCode);
            fixture.Authentication.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("count")]
        [InlineData("tooMany")]
        [InlineData("duplicate")]
        [InlineData("order")]
        [InlineData("submission")]
        [InlineData("path")]
        [InlineData("date")]
        [InlineData("latitude")]
        [InlineData("longitude")]
        [InlineData("outside")]
        [InlineData("cars")]
        [InlineData("fields")]
        [InlineData("flags")]
        [InlineData("length")]
        public async Task InvalidPhotoSetsAreRejectedBeforeCommit(string invalid)
        {
            using Fixture fixture = new Fixture();
            var request = fixture.Prepare(2);
            switch (invalid)
            {
                case "count":
                    request.Photos.Clear();
                    break;
                case "tooMany":
                    request.Photos.AddRange([request.Photos[0], request.Photos[0], request.Photos[0]]);
                    break;
                case "duplicate":
                    request.Photos[1].PhotoId = request.Photos[0].PhotoId;
                    break;
                case "order":
                    request.Photos[1].PhotoNumber = 0;
                    break;
                case "submission":
                    request.Photos[1].SubmissionId = "another";
                    break;
                case "path":
                    request.Photos[0].PhotoId = "../other";
                    break;
                case "date":
                    request.Photos[0].PhotoDateTime = null;
                    break;
                case "latitude":
                    request.Photos[0].PhotoLatitude = "NaN";
                    break;
                case "longitude":
                    request.Photos[0].PhotoLongitude = "-Infinity";
                    break;
                case "outside":
                    request.Photos[0].PhotoLatitude = "48";
                    break;
                case "cars":
                    request.Photos[0].NumberOfCars = 0;
                    break;
                case "fields":
                    request.Photos[1].NumberOfCars = 3;
                    break;
                case "flags":
                    request.Photos[1].UserSpecifiedLocation = true;
                    break;
                case "length":
                    request.Photos[0].PhotoCrossStreet = new string('x', 1025);
                    break;
            }
            Assert.IsType<BadRequestObjectResult>(await fixture.Controller.FinalizeUpload(request));
            Assert.Null(fixture.Committed);
        }

        [Theory]
        [InlineData("report")]
        [InlineData("device")]
        [InlineData("set")]
        [InlineData("photo")]
        [InlineData("order")]
        public async Task PreparationMustMatchStoredReportContextAndSet(string mismatch)
        {
            using Fixture fixture = new Fixture();
            var request = fixture.Prepare();
            var stored = fixture.Metadata["photo-0"];
            switch (mismatch)
            {
                case "report":
                    stored.ReportId = new string('a', 32);
                    break;
                case "device":
                    stored.DeviceId = "different-device";
                    break;
                case "set":
                    stored.SubmissionId = "other";
                    break;
                case "photo":
                    stored.PhotoId = "other";
                    break;
                case "order":
                    stored.PhotoNumber = 3;
                    break;
            }
            Assert.IsType<BadRequestObjectResult>(await fixture.Controller.FinalizeUpload(request));
            Assert.Null(fixture.Committed);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(8 * 1024 * 1024 + 1)]
        public async Task SourceSizeLimitIsCheckedBeforeStorage(long length)
        {
            using Fixture fixture = new Fixture();
            var request = fixture.Prepare();
            fixture.SourceLength = length;
            Assert.IsType<BadRequestObjectResult>(await fixture.Controller.FinalizeUpload(request));
            Assert.Null(fixture.Committed);
        }

        [Theory]
        [InlineData(404, 410, UploadErrors.PreparationExpired)]
        [InlineData(412, 410, UploadErrors.PreparationExpired)]
        [InlineData(503, 503, UploadErrors.ProviderUnavailable)]
        public async Task MissingOrChangedPreparationDiffersFromStorageOutage(int storageStatus, int status, string code)
        {
            using Fixture fixture = new Fixture();
            var request = fixture.Prepare();
            fixture.Blobs.Setup(b => b.GetBlobClient("initialupload/photo-0.json"))
                .Throws(new RequestFailedException(storageStatus, "private storage detail"));
            AssertError(await fixture.Controller.FinalizeUpload(request), status, code);
            Assert.Null(fixture.Committed);
        }

        [Fact]
        public async Task DeviceHeaderBindsCanonicalMetadataAndAcceptedContext()
        {
            using Fixture fixture = new Fixture();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = "installation-1";
            var request = fixture.Prepare();
            fixture.Metadata["photo-0"].DeviceId = "installation-1";
            Assert.IsType<OkObjectResult>(await fixture.Controller.FinalizeUpload(request));
            Assert.Equal("installation-1", fixture.LastDeviceId);
            Assert.Equal("installation-1", Assert.Single(fixture.Committed!).Metadata.DeviceId);
        }

        [Theory]
        [InlineData("")]
        [InlineData("invalid/report")]
        [InlineData("not-hex")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF")]
        public async Task InvalidReportIdIsRejectedBeforeStorage(string id)
        {
            using Fixture fixture = new Fixture();
            fixture.Controller.Request.Headers[UploadController.ReportIdHeader] = id;
            Assert.IsType<BadRequestObjectResult>(await fixture.Controller.FinalizeUpload(fixture.Prepare()));
            fixture.Store.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData("")]
        [InlineData("bad/device")]
        [InlineData("two,devices")]
        public async Task InvalidDeviceContextCannotBeUsedToClaimAReport(string device)
        {
            using Fixture fixture = new Fixture();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = device;
            Assert.IsType<BadRequestObjectResult>(await fixture.Controller.FinalizeUpload(fixture.Prepare()));
            fixture.Store.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SlackFailureCannotUndoAcceptance()
        {
            using Fixture fixture = new Fixture();
            fixture.SlackHandler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>()).ThrowsAsync(new HttpRequestException());
            Assert.IsType<OkObjectResult>(await fixture.Controller.FinalizeUpload(fixture.Prepare()));
            Assert.NotNull(fixture.Committed);
        }

        [Fact]
        public async Task InitialRejectsImageValidationAsReadable400InsteadOfFaultedTask500()
        {
            using Fixture fixture = new Fixture();
            byte[] bytes = Encoding.UTF8.GetBytes("not an image");
            List<IFormFile> files = new List<IFormFile>() { new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", "bad.jpg") };
            var result = Assert.IsType<BadRequestObjectResult>(await fixture.Controller.UploadPhoto(files));
            Assert.Contains("Photo 1", Assert.IsType<string>(result.Value));
        }

        [Fact]
        public async Task InitialChecksDeclaredByteLimitsBeforeReading()
        {
            using Fixture fixture = new Fixture();
            Mock<IFormFile> file = new Mock<IFormFile>(MockBehavior.Strict);
            file.SetupGet(f => f.Length).Returns(30_000_001);
            Assert.IsType<BadRequestObjectResult>(await fixture.Controller.UploadPhoto([file.Object]));
            file.Verify(f => f.OpenReadStream(), Times.Never);
        }

        [Fact]
        public async Task InitialAcceptsOriginalsLargerThanThePreparedJpegLimit()
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            using MagickImage original = new MagickImage(MagickColors.White, 2048, 1600);
            byte[] bitmap = original.ToByteArray(MagickFormat.Bmp);
            Assert.True(bitmap.Length > 8 * 1024 * 1024);

            var result = Assert.IsType<OkObjectResult>(await fixture.Controller.UploadPhoto(
                [new FormFile(new MemoryStream(bitmap), 0, bitmap.Length, "files", "large.bmp")]));

            Assert.Single(Assert.IsType<InitialPhotoUpload[]>(result.Value));
            Assert.Single(fixture.InitialMetadata);
        }

        [Fact]
        public async Task InitialBindsStableReportAndDeviceToFreshPreparationAndPreservesAnalysis()
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            fixture.Controller.Request.Headers[UploadController.DeviceIdHeader] = "installation-1";
            byte[] jpeg = CreateJpeg();
            var result = Assert.IsType<OkObjectResult>(await fixture.Controller.UploadPhoto(
                [new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", "photo.jpg")]));
            InitialPhotoUpload response = Assert.Single(Assert.IsType<InitialPhotoUpload[]>(result.Value));
            InitialPhotoUploadMetadata stored = Assert.Single(fixture.InitialMetadata);
            Assert.Equal(Id, stored.ReportId);
            Assert.Equal("installation-1", stored.DeviceId);
            Assert.NotEqual(Id, stored.SubmissionId);
            Assert.Equal(stored.SubmissionId, response.SubmissionId);
            Assert.Equal(stored.PhotoId, response.PhotoId);
            Assert.Equal(0, response.PhotoNumber);
            Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0), response.PhotoDateTime);
            Assert.Equal("car", Assert.Single(response.Tags).Name);
            Assert.StartsWith("https://account.blob.core.windows.net/uploads/initialupload/", response.Uri);
            Assert.Contains("sig=", response.Uri);
        }

        [Fact]
        public async Task InitialProviderFailureIs503AndDoesNotExposeItsException()
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            fixture.Analysis.Setup(a => a.AnalyzeAsync(It.IsAny<BinaryData>(), VisualFeatures.Tags,
                    It.IsAny<ImageAnalysisOptions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(503, "sensitive private upstream detail"));
            byte[] jpeg = CreateJpeg();
            AssertError(await fixture.Controller.UploadPhoto(
                [new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", "photo.jpg")]),
                503, UploadErrors.ProviderUnavailable);
        }

        [Fact]
        public async Task InitialCollectsAsyncValidationErrorsWithoutLeakingFailedTasks()
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            fixture.Safety.Setup(s => s.AnalyzeImageAsync(It.IsAny<BinaryData>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(ContentSafetyModelFactory.AnalyzeImageResult(
                    [ContentSafetyModelFactory.ImageCategoriesAnalysis(ImageCategory.Hate, 6)]), Mock.Of<Response>()));
            byte[] invalid = Encoding.UTF8.GetBytes("not an image");
            byte[] jpeg = CreateJpeg();
            var result = Assert.IsType<BadRequestObjectResult>(await fixture.Controller.UploadPhoto(
            [
                new FormFile(new MemoryStream(invalid), 0, invalid.Length, "files", "invalid.jpg"),
                new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", "photo.jpg")
            ]));
            string message = Assert.IsType<string>(result.Value);
            Assert.Contains("Photo 1", message);
            Assert.Contains("Photo 2: Photo does not pass content check.", message);
            Assert.Empty(fixture.InitialMetadata);
        }

        [Fact]
        public async Task InitialPreservesExifSeattleBoundsValidation()
        {
            using Fixture fixture = new Fixture();
            using MagickImage image = new MagickImage(MagickColors.White, 16, 16);
            ExifProfile profile = new ExifProfile();
            profile.SetValue(ExifTag.GPSLatitude, [new Rational(50), new Rational(0), new Rational(0)]);
            profile.SetValue(ExifTag.GPSLatitudeRef, "N");
            profile.SetValue(ExifTag.GPSLongitude, [new Rational(122), new Rational(0), new Rational(0)]);
            profile.SetValue(ExifTag.GPSLongitudeRef, "W");
            image.SetProfile(profile);
            byte[] jpeg = image.ToByteArray(MagickFormat.Jpeg);
            var result = Assert.IsType<BadRequestObjectResult>(await fixture.Controller.UploadPhoto(
                [new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", "photo.jpg")]));
            AssertPhotoLocationLink(result.Value, 50, -122);
            fixture.Analysis.VerifyNoOtherCalls();
            fixture.Safety.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("CreateDate", "2026-09-07T10:00:00-07:00", false)]
        [InlineData("DateTimeDigitized", "2026-09-07T10:00:00+14:00", true)]
        [InlineData("DateTimeOriginal", "2026-09-07T10:00:00Z", false)]
        public async Task InitialReadsXmpOnlyDatesAndSeattleGpsWithoutConvertingWallClock(
            string field, string date, bool elements)
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            string xmp = Xmp(elements, (field, date),
                ("GPSLatitude", "47,36.3726N"), ("GPSLongitude", "122,19.9242W"));

            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture, MetadataJpeg(xmp));

            Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0), photo.PhotoDateTime);
            Assert.Equal(DateTimeKind.Unspecified, photo.PhotoDateTime!.Value.Kind);
            AssertCoordinate(47.60621, photo.PhotoLatitude);
            AssertCoordinate(-122.33207, photo.PhotoLongitude);
            Assert.Equal("Pike St", photo.PhotoCrossStreet);
        }

        [Theory]
        [InlineData("47,36.3726N", "122,19.9242W")]
        [InlineData("47,36,22.356N", "122,19,55.452W")]
        [InlineData("47 deg 36' 22.356\" N", "122 deg 19' 55.452\" W")]
        [InlineData("47° 36′ 22.356″ N", "122° 19′ 55.452″ W")]
        [InlineData("47 36 22356/1000 N", "122 19 55452/1000 W")]
        [InlineData("47.60621", "-122.33207")]
        public async Task InitialReadsFractionalXmpCoordinateFormats(string latitude, string longitude)
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture,
                MetadataJpeg(Xmp(false, ("GPSLatitude", latitude), ("GPSLongitude", longitude))));
            AssertCoordinate(47.60621, photo.PhotoLatitude);
            AssertCoordinate(-122.33207, photo.PhotoLongitude);
        }

        [Theory]
        [InlineData("50,0N", "122,0W", 50, -122)]
        [InlineData("47,36.3726S", "122,19.9242W", -47.60621, -122.33207)]
        [InlineData("47,36.3726N", "122,19.9242E", 47.60621, 122.33207)]
        public async Task InitialRejectsOutsideSeattleXmpBeforeCallingAi(
            string latitude, string longitude, double expectedLatitude, double expectedLongitude)
        {
            using Fixture fixture = new Fixture();
            byte[] jpeg = MetadataJpeg(Xmp(false, ("GPSLatitude", latitude), ("GPSLongitude", longitude)));
            var result = Assert.IsType<BadRequestObjectResult>(await fixture.Controller.UploadPhoto(
                [new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", "xmp.jpg")]));
            AssertPhotoLocationLink(result.Value, expectedLatitude, expectedLongitude);
            fixture.Analysis.VerifyNoOtherCalls();
            fixture.Safety.VerifyNoOtherCalls();
            fixture.MapsHandler.VerifyNoOtherCalls();
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("fr-FR")]
        public async Task OutsideSeattleMapLinkPreservesCoordinatesIndependentOfServerCulture(string culture)
        {
            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using Fixture fixture = new Fixture();
                byte[] jpeg = MetadataJpeg(Xmp(false,
                    ("GPSLatitude", "50.123456789"), ("GPSLongitude", "-122.3456789")));
                var result = Assert.IsType<BadRequestObjectResult>(await fixture.Controller.UploadPhoto(
                    [new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", "location.jpg")]));

                AssertPhotoLocationLink(result.Value, 50.123456789, -122.3456789);
                fixture.Analysis.VerifyNoOtherCalls();
                fixture.Safety.VerifyNoOtherCalls();
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        private static void AssertPhotoLocationLink(object? value, double latitude, double longitude)
        {
            string message = Assert.IsType<string>(value);
            Assert.StartsWith("Photo 1: Photo not taken in Seattle.", message);
            XElement? link = XElement.Parse($"<error>{message}</error>").Element("a");
            Assert.NotNull(link);
            Uri map = new Uri((string)link.Attribute("href")!);
            Assert.Equal("https", map.Scheme);
            Assert.Equal("bing.com", map.Host);
            Assert.Equal("/maps", map.AbsolutePath);
            var query = QueryHelpers.ParseQuery(map.Query);
            string[] coordinates = query["cp"].ToString().Split('~');
            Assert.Equal(2, coordinates.Length);
            Assert.Equal(latitude, double.Parse(coordinates[0], CultureInfo.InvariantCulture), 8);
            Assert.Equal(longitude, double.Parse(coordinates[1], CultureInfo.InvariantCulture), 8);
            Assert.Equal("13", query["lvl"].ToString());
            Assert.Equal($"point.{coordinates[0]}_{coordinates[1]}_Photo location___", query["sp"].ToString());
            Assert.Equal($"{latitude.ToString("0.#####", CultureInfo.InvariantCulture)}, " +
                longitude.ToString("0.#####", CultureInfo.InvariantCulture), link.Value);
            Assert.Equal("_blank", (string?)link.Attribute("target"));
            Assert.Equal("noopener noreferrer", (string?)link.Attribute("rel"));
        }

        [Fact]
        public async Task InitialPrefersValidExifOverConflictingXmp()
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            ExifProfile exif = SeattleExif();
            exif.SetValue(ExifTag.DateTimeDigitized, "2025:01:02 03:04:05");
            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture, MetadataJpeg(Xmp(false,
                ("CreateDate", "2026-09-07T10:00:00Z"), ("GPSLatitude", "50,0N"), ("GPSLongitude", "122,0E")), exif));
            Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5), photo.PhotoDateTime);
            AssertCoordinate(47.6, photo.PhotoLatitude);
            AssertCoordinate(-(122 + 20.0 / 60), photo.PhotoLongitude);
        }

        [Theory]
        [InlineData("date")]
        [InlineData("location")]
        [InlineData("latitude")]
        [InlineData("invalid")]
        public async Task InitialFillsOnlyMissingOrInvalidExifFieldsFromXmp(string exifFields)
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            ExifProfile exif = exifFields == "location" ? SeattleExif() : new ExifProfile();
            if (exifFields == "date")
            {
                exif.SetValue(ExifTag.DateTimeDigitized, "2025:01:02 03:04:05");
            }

            if (exifFields is "latitude" or "invalid")
            {
                exif.SetValue(ExifTag.GPSLatitude,
                                                    [new Rational(47), new Rational(exifFields == "invalid" ? 60 : 36), new Rational(0)]);
                exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            }
            if (exifFields == "invalid")
            {
                exif.SetValue(ExifTag.DateTimeDigitized, "not-a-date");
            }

            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture, MetadataJpeg(Xmp(false,
                ("CreateDate", "2026-09-07T10:00:00.125-07:00"), ("GPSLatitude", "47,36.3726N"),
                ("GPSLongitude", "122,19.9242W")), exif));
            Assert.Equal(exifFields == "date" ? new DateTime(2025, 1, 2, 3, 4, 5) :
                new DateTime(2026, 9, 7, 10, 0, 0, 125), photo.PhotoDateTime);
            AssertCoordinate(exifFields is "location" or "latitude" ? 47.6 : 47.60621, photo.PhotoLatitude);
            AssertCoordinate(exifFields == "location" ? -(122 + 20.0 / 60) : -122.33207, photo.PhotoLongitude);
            if (exifFields == "invalid")
            {
                AssertMetadataWarning(fixture, "EXIF");
            }
        }

        [Fact]
        public async Task InitialReadsSeparateXmpReferencesAndTriesAlternateCaptureDates()
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture, MetadataJpeg(Xmp(false,
                ("CreateDate", "not-a-date"), ("DateTimeOriginal", "2026-09-07T10:00:00.125Z"),
                ("GPSLatitude", "47,36.3726"), ("GPSLatitudeRef", "N"),
                ("GPSLongitude", "122,19.9242"), ("GPSLongitudeRef", "W"))));
            Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0, 125), photo.PhotoDateTime);
            AssertCoordinate(47.60621, photo.PhotoLatitude);
            AssertCoordinate(-122.33207, photo.PhotoLongitude);
            AssertMetadataWarning(fixture, "XMP");
        }

        [Fact]
        public async Task InitialLeavesMissingMetadataForTheReportForm()
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture, MetadataJpeg());
            Assert.Null(photo.PhotoDateTime);
            Assert.Null(photo.PhotoLatitude);
            Assert.Null(photo.PhotoLongitude);
            Assert.DoesNotContain(fixture.Log.Invocations, i => i.Method.Name == "Log");
        }

        [Theory]
        [InlineData("<broken>")]
        [InlineData("<!DOCTYPE x [<!ENTITY location SYSTEM 'https://not-requested.invalid/private'>]><x>&location;</x>")]
        public async Task InitialLogsMalformedOrUnsafeXmpWithoutDiscardingValidExif(string xmp)
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            ExifProfile exif = new ExifProfile();
            exif.SetValue(ExifTag.DateTimeDigitized, "2025:01:02 03:04:05");
            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture, MetadataJpeg(xmp, exif));
            Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5), photo.PhotoDateTime);
            Assert.Null(photo.PhotoLatitude);
            Assert.Null(photo.PhotoLongitude);
            AssertMetadataWarning(fixture, "XMP");
            Assert.DoesNotContain(fixture.Log.Invocations, i =>
                i.Method.Name == "Log" && i.Arguments[2].ToString()!.Contains("not-requested.invalid"));
        }

        [Theory]
        [InlineData("47,60N")]
        [InlineData("NaN")]
        [InlineData("47,36,1/0N")]
        [InlineData("47,36E")]
        public async Task InitialLogsMalformedXmpGpsAndLeavesLocationUnset(string latitude)
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture,
                MetadataJpeg(Xmp(false, ("GPSLatitude", latitude), ("GPSLongitude", "122,19.9242W"))));
            Assert.Null(photo.PhotoLatitude);
            Assert.Null(photo.PhotoLongitude);
            AssertMetadataWarning(fixture, "XMP");
        }

        [Fact]
        public async Task InitialBoundsXmpNestingAndIgnoresUnrelatedNamespaces()
        {
            using Fixture fixture = new Fixture();
            fixture.ConfigureInitial();
            string deep = string.Concat(Enumerable.Repeat("<nested>", 40)) +
                string.Concat(Enumerable.Repeat("</nested>", 40));
            InitialPhotoUpload photo = await UploadMetadataPhoto(fixture, MetadataJpeg(deep));
            Assert.Null(photo.PhotoDateTime);
            AssertMetadataWarning(fixture, "XMP");
            fixture.InitialMetadata.Clear();
            photo = await UploadMetadataPhoto(fixture, MetadataJpeg(
                "<other xmlns='https://not-xmp.invalid/' CreateDate='2026-09-07T10:00:00Z' GPSLatitude='50,0N' GPSLongitude='122,0W'/>"));
            Assert.Null(photo.PhotoDateTime);
            Assert.Null(photo.PhotoLatitude);
        }

        [Fact]
        public async Task InProgressHasTypedRetryableResponseAndRetryAfter()
        {
            using Fixture fixture = new Fixture();
            fixture.Store.Setup(s => s.GetAsync(Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ReportInProgressException(TimeSpan.FromSeconds(2.5)));
            AssertError(await fixture.Controller.ReportStatus(Id), 409, UploadErrors.ReportInProgress);
            Assert.Equal("3", fixture.Controller.Response.Headers.RetryAfter);
            AssertError(await fixture.Controller.FinalizeUpload(fixture.Prepare()), 409, UploadErrors.ReportInProgress);
            fixture.Authentication.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task HttpRoutesBindSharedEnvelopeMultipartAndReceiptWithoutLegacyAlias()
        {
            using Fixture fixture = new Fixture();
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddControllers().AddApplicationPart(typeof(UploadController).Assembly).AddControllersAsServices();
            builder.Services.AddSingleton<IAuthenticationService>(fixture.Authentication.Object);
            builder.Services.AddTransient(_ => fixture.NewController());
            await using WebApplication app = builder.Build();
            app.MapControllers();
            await app.StartAsync();
            using HttpClient client = new HttpClient() { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add(UploadController.ReportIdHeader, Id);

            var request = fixture.Prepare();
            client.DefaultRequestHeaders.Add(UploadController.DeviceIdHeader, BlockedInstallation);
            fixture.DeviceBlocked = true;
            using (MultipartFormDataContent blockedPhotos = new MultipartFormDataContent())
            {
                blockedPhotos.Add(new ByteArrayContent([1]), "files", "photo.jpg");
                var blockedInitial = await client.PostAsync("/api/Upload/Initial", blockedPhotos);
                Assert.Equal(HttpStatusCode.Forbidden, blockedInitial.StatusCode);
                Assert.Equal("This device can't submit reports.", await blockedInitial.Content.ReadAsStringAsync());
            }
            var blockedFinalize = await client.PostAsJsonAsync("/api/Upload/Finalize", request);
            Assert.Equal(HttpStatusCode.Forbidden, blockedFinalize.StatusCode);
            Assert.Equal("This device can't submit reports.", await blockedFinalize.Content.ReadAsStringAsync());
            fixture.Existing = new SubmissionReport(fixture.Receipt, BlockedInstallation, []);
            Assert.Equal(fixture.Receipt, await client.GetFromJsonAsync<SubmissionReceipt>($"/api/Upload/Reports/{Id}"));
            var blockedRetry = await client.PostAsJsonAsync("/api/Upload/Finalize", request);
            Assert.Equal(HttpStatusCode.OK, blockedRetry.StatusCode);
            Assert.Equal(fixture.Receipt, await blockedRetry.Content.ReadFromJsonAsync<SubmissionReceipt>());
            fixture.Existing = null;
            fixture.DeviceBlocked = false;
            client.DefaultRequestHeaders.Remove(UploadController.DeviceIdHeader);

            var invalidEnvelope = await client.PostAsync("/api/Upload/Finalize",
                new StringContent("""{"photos":null,"attribution":null}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalidEnvelope.StatusCode);
            var response = await client.PostAsJsonAsync("/api/Upload/Finalize", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(fixture.Receipt, await response.Content.ReadFromJsonAsync<SubmissionReceipt>());

            fixture.Existing = new SubmissionReport(fixture.Receipt, null, []);
            Assert.Equal(fixture.Receipt, await client.GetFromJsonAsync<SubmissionReceipt>($"/api/Upload/Reports/{Id}"));
            var retry = await client.PostAsJsonAsync("/api/Upload/Finalize", new
            {
                photos = Array.Empty<object>(),
                attribution = new
                {
                }
            });
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var unpreparedRetry = await client.PostAsync("/api/Upload/Finalize",
                new StringContent("""{"photos":null,"attribution":null}""", Encoding.UTF8, "application/json"));
            Assert.True(unpreparedRetry.IsSuccessStatusCode, await unpreparedRetry.Content.ReadAsStringAsync());
            var legacy = await client.PostAsJsonAsync("/api/Upload/Finalize", request.Photos);
            Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
            var alias = await client.PostAsJsonAsync("/api/Upload/FinalizeMobile", request);
            Assert.Equal(HttpStatusCode.NotFound, alias.StatusCode);

            using MultipartFormDataContent multipart = new MultipartFormDataContent();
            multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("not an image")), "files", "invalid.jpg");
            var initial = await client.PostAsync("/api/Upload/Initial", multipart);
            Assert.Equal(HttpStatusCode.BadRequest, initial.StatusCode);
            Assert.Contains("Photo 1", await initial.Content.ReadAsStringAsync());

            fixture.ConfigureInitial();
            using MultipartFormDataContent validMultipart = new MultipartFormDataContent();
            validMultipart.Add(new ByteArrayContent(CreateJpeg()), "files", "photo.jpg");
            var prepared = await client.PostAsync("/api/Upload/Initial", validMultipart);
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
            Assert.Single((await prepared.Content.ReadFromJsonAsync<InitialPhotoUpload[]>())!);

            client.DefaultRequestHeaders.Remove(UploadController.ReportIdHeader);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/Upload/Finalize", request)).StatusCode);
            await app.StopAsync();
        }

        private static byte[] CreateJpeg()
        {
            using MagickImage image = new MagickImage(MagickColors.White, 16, 16);
            ExifProfile profile = new ExifProfile();
            profile.SetValue(ExifTag.DateTimeDigitized, "2026:09:07 10:00:00");
            image.SetProfile(profile);
            return image.ToByteArray(MagickFormat.Jpeg);
        }

        private static string Xmp(bool elements, params (string Name, string Value)[] fields)
        {
            XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
            XNamespace exif = "http://ns.adobe.com/exif/1.0/";
            XElement description = new XElement(rdf + "Description", new XAttribute(XNamespace.Xmlns + "xmp", xmp),
                new XAttribute(XNamespace.Xmlns + "exif", exif));
            foreach (var field in fields)
            {
                XName name = (field.Name == "CreateDate" ? xmp : exif) + field.Name;
                description.Add(elements ? (object)new XElement(name, field.Value) : new XAttribute(name, field.Value));
            }
            return new XElement(XName.Get("xmpmeta", "adobe:ns:meta/"),
                new XElement(rdf + "RDF", description)).ToString(SaveOptions.DisableFormatting);
        }

        private static byte[] MetadataJpeg(string? xmp = null, ExifProfile? exif = null)
        {
            using MagickImage image = new MagickImage(MagickColors.White, 16, 16);
            if (exif is not null)
            {
                image.SetProfile(exif);
            }

            byte[] jpeg = image.ToByteArray(MagickFormat.Jpeg);
            if (xmp is null)
            {
                return jpeg;
            }
            // Inject a genuine JPEG APP1 packet without ImageMagick synchronizing conflicting EXIF/XMP.
            byte[] packet = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmp);
            int length = packet.Length + 2;
            Assert.InRange(length, 2, ushort.MaxValue);
            using MemoryStream output = new MemoryStream();
            output.Write(jpeg.AsSpan(0, 2));
            output.WriteByte(0xff);
            output.WriteByte(0xe1);
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)length);
            output.Write(packet);
            output.Write(jpeg.AsSpan(2));
            return output.ToArray();
        }

        private static ExifProfile SeattleExif()
        {
            ExifProfile exif = new ExifProfile();
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(47), new Rational(36), new Rational(0)]);
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(122), new Rational(20), new Rational(0)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "W");
            return exif;
        }

        private static async Task<InitialPhotoUpload> UploadMetadataPhoto(Fixture fixture, byte[] jpeg)
        {
            var result = Assert.IsType<OkObjectResult>(await fixture.Controller.UploadPhoto(
                                        [new FormFile(new MemoryStream(jpeg), 0, jpeg.Length, "files", "metadata.jpg")]));
            return Assert.Single(Assert.IsType<InitialPhotoUpload[]>(result.Value));
        }

        private static void AssertCoordinate(double expected, string? actual)
        {
            Assert.Equal(expected, double.Parse(actual!, CultureInfo.InvariantCulture), precision: 7);
        }

        private static void AssertMetadataWarning(Fixture fixture, string format)
        {
            Assert.Contains(fixture.Log.Invocations, i => i.Method.Name == "Log" &&
                                        i.Arguments[0].Equals(LogLevel.Warning) && i.Arguments[2].ToString()!.Contains(format));
        }

        private static void AssertError(IActionResult result, int status, string code)
        {
            var response = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, Assert.IsType<UploadError>(response.Value).Code);
            Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(response.Value));
            Assert.DoesNotContain("private", JsonSerializer.Serialize(response.Value));
        }

        private sealed class Fixture : IDisposable
        {
            public Mock<BlobContainerClient> Blobs { get; } = new Mock<BlobContainerClient>(MockBehavior.Strict);
            public Mock<ReportStore> Store { get; }
            public Mock<BlockedDevicesDatabase> BlockedDevices { get; } = new Mock<BlockedDevicesDatabase>(
                MockBehavior.Strict, NullLogger<BlockedDevicesDatabase>.Instance, Mock.Of<Cosmos.Container>());
            public bool DeviceBlocked { get; set; }
            public Mock<IAuthenticationService> Authentication { get; } = new Mock<IAuthenticationService>(MockBehavior.Strict);
            public Mock<HttpMessageHandler> Provider { get; } = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            public Mock<HttpMessageHandler> SlackHandler { get; } = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            public Mock<ImageAnalysisClient> Analysis { get; } = new Mock<ImageAnalysisClient>(MockBehavior.Strict);
            public Mock<ContentSafetyClient> Safety { get; } = new Mock<ContentSafetyClient>(MockBehavior.Strict);
            public Mock<HttpMessageHandler> MapsHandler { get; } = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            public Mock<ILogger<UploadController>> Log { get; } = new Mock<ILogger<UploadController>>();
            public List<InitialPhotoUploadMetadata> InitialMetadata { get; } = [];
            public Dictionary<string, InitialPhotoUploadMetadata> Metadata { get; } = [];
            public AuthenticateResult Cookie { get; set; } = AuthenticateResult.NoResult();
            public AuthenticateResult Bearer { get; set; } = AuthenticateResult.NoResult();
            public SubmissionReceipt Receipt { get; } = new SubmissionReceipt(Id, Id,
                new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero), new ReportAttribution());
            public SubmissionReport? Existing { get; set; }
            public IReadOnlyList<PreparedSubmissionPhoto>? Committed { get; private set; }
            public ReportAttribution? CommittedAttribution { get; private set; }
            public long SourceLength { get; set; } = 23;
            public string? LastDeviceId { get; private set; }
            public UploadController Controller { get; }
            private readonly ServiceProvider services;
            private readonly HttpClient providerClient;
            private readonly HttpClient slackClient;
            private readonly HttpClient mapsClient;
            private readonly SlackbotProvider slack;

            public Fixture()
            {
                BlockedDevices.Setup(d => d.IsBlockedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => DeviceBlocked);
                Store = new Mock<ReportStore>(Blobs.Object, NullLogger<ReportStore>.Instance, TimeProvider.System);
                Store.Setup(s => s.GetAsync(Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .Returns((string _, string? device, CancellationToken _) =>
                    {
                        LastDeviceId = device;
                        return Task.FromResult(Existing);
                    });
                Store.Setup(s => s.CommitAsync(Id, It.IsAny<string?>(), It.IsAny<ReportAttribution>(),
                    It.IsAny<IReadOnlyList<PreparedSubmissionPhoto>>(), It.IsAny<CancellationToken>()))
                    .Returns((string _, string? device, ReportAttribution attribution,
                        IReadOnlyList<PreparedSubmissionPhoto> photos, CancellationToken _) =>
                    {
                        LastDeviceId = device;
                        Committed = photos;
                        CommittedAttribution = attribution;
                        return Task.FromResult(Receipt);
                    });
                Authentication.Setup(a => a.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>()))
                    .Returns((HttpContext _, string scheme) => Task.FromResult(scheme == BlueskyAuthDefaults.BearerScheme ? Bearer : Cookie));
                services = new ServiceCollection().AddSingleton(Authentication.Object).BuildServiceProvider();
                Provider.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
                SlackHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
                MapsHandler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
                providerClient = new HttpClient(Provider.Object);
                slackClient = new HttpClient(SlackHandler.Object);
                mapsClient = new HttpClient(MapsHandler.Object);
                SlackHandler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK));
                Mock<SecretClient> secrets = new Mock<SecretClient>();
                secrets.Setup(s => s.GetSecret(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .Returns((string name, string _, CancellationToken _) =>
                        Response.FromValue(new KeyVaultSecret(name, "test-value"), Mock.Of<Response>()));
                slack = new SlackbotProvider(NullLogger<SlackbotProvider>.Instance, slackClient, secrets.Object);
                Controller = NewController();
            }

            public UploadController NewController()
            {
                UploadController controller = new UploadController(Log.Object,
                                                    Analysis.Object, Safety.Object,
                                                    new MapsSearchClient(new AzureKeyCredential("test-key"),
                                                        new MapsSearchClientOptions() { Transport = new HttpClientTransport(mapsClient) }), Blobs.Object,
                                                    new MastodonCredentialVerifier(providerClient), slack,
                                                    new DeviceBlocklistProvider(NullLogger<DeviceBlocklistProvider>.Instance, BlockedDevices.Object),
                                                    Store.Object, new HelperMethods())
                {
                    ControllerContext = new ControllerContext() { HttpContext = new DefaultHttpContext() { RequestServices = services } }
                };
                controller.Request.Headers[UploadController.ReportIdHeader] = Id;
                return controller;
            }

            public void ConfigureInitial()
            {
                MapsHandler.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                                                        ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                                                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
                                                    {
                                                        Content = new StringContent("""
                        {"summary":{"queryType":"NEARBY","queryTime":1,"numResults":1},
                         "addresses":[{"address":{"streetName":"Pike St"},"position":"47.60621,-122.33207"}]}
                        """, Encoding.UTF8, "application/json")
                                                    });
                Analysis.Setup(a => a.AnalyzeAsync(It.IsAny<BinaryData>(), VisualFeatures.Tags,
                        It.IsAny<ImageAnalysisOptions>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(AIVisionImageAnalysisModelFactory.ImageAnalysisResult(
                        tags: AIVisionImageAnalysisModelFactory.TagsResult(
                            [AIVisionImageAnalysisModelFactory.DetectedTag(.95f, "car")])), Mock.Of<Response>()));
                Safety.Setup(s => s.AnalyzeImageAsync(It.IsAny<BinaryData>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(ContentSafetyModelFactory.AnalyzeImageResult(
                        [ContentSafetyModelFactory.ImageCategoriesAnalysis(ImageCategory.Hate, 0)]), Mock.Of<Response>()));
                Mock<BlobServiceClient> service = new Mock<BlobServiceClient>();
                service.SetupGet(s => s.AccountName).Returns("account");
                service.Setup(s => s.GetUserDelegationKeyAsync(It.IsAny<DateTimeOffset?>(),
                        It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(BlobsModelFactory.UserDelegationKey(
                        Guid.Empty.ToString(), Guid.Empty.ToString(),
                        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
                        "b", "2023-11-03", Convert.ToBase64String(new byte[32])),
                        Mock.Of<Response>()));
                Blobs.Protected().Setup<BlobServiceClient>("GetParentBlobServiceClientCore").Returns(service.Object);
                Blobs.Setup(b => b.GetBlobClient(It.Is<string>(name => name.StartsWith("initialupload/"))))
                    .Returns((string name) =>
                    {
                        Mock<BlobClient> blob = new Mock<BlobClient>();
                        blob.SetupGet(b => b.Uri).Returns(new Uri($"https://account.blob.core.windows.net/uploads/{name}"));
                        blob.SetupGet(b => b.Name).Returns(name);
                        blob.SetupGet(b => b.BlobContainerName).Returns("uploads");
                        blob.Protected().Setup<BlobContainerClient>("GetParentBlobContainerClientCore").Returns(Blobs.Object);
                        blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
                            .Returns((BinaryData data, BlobUploadOptions options, CancellationToken _) =>
                            {
                                Assert.Equal(ETag.All, options.Conditions.IfNoneMatch);
                                if (name.EndsWith(".json"))
                                {
                                    InitialMetadata.Add(data.ToObjectFromJson<InitialPhotoUploadMetadata>()!);
                                    Assert.Equal("application/json", options.HttpHeaders.ContentType);
                                }
                                else
                                {
                                    Assert.Equal("image/jpeg", options.HttpHeaders.ContentType);
                                    Assert.InRange(data.ToMemory().Length, 1, 8 * 1024 * 1024);
                                    using MagickImage jpeg = new MagickImage(data.ToArray());
                                    Assert.Equal(MagickFormat.Jpeg, jpeg.Format);
                                }
                                return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContentInfo(
                                    new ETag("\"initial-version\""), DateTimeOffset.UtcNow, null!, null!, 0),
                                    Mock.Of<Response>()));
                            });
                        return blob.Object;
                    });
            }

            public FinalizeReportRequest Prepare(int count = 1)
            {
                List<FinalizedPhotoUpload> photos = [];
                for (int i = 0; i < count; i++)
                {
                    string photoId = $"photo-{i}";
                    InitialPhotoUploadMetadata metadata = new InitialPhotoUploadMetadata()
                    {
                        PhotoId = photoId,
                        SubmissionId = "prepared-set",
                        PhotoNumber = i,
                        ReportId = Id,
                        Tags = [new ImageTag() { Name = "car", Confidence = .95f }]
                    };
                    Metadata[photoId] = metadata;
                    photos.Add(new FinalizedPhotoUpload()
                    {
                        PhotoId = photoId,
                        SubmissionId = "prepared-set",
                        PhotoNumber = i,
                        PhotoDateTime = new DateTime(2026, 9, 7, 10, 0, 0),
                        PhotoLatitude = "47.60621",
                        PhotoLongitude = "-122.33207",
                        PhotoCrossStreet = "Pike St",
                        NumberOfCars = 2
                    });
                    Mock<BlobClient> json = new Mock<BlobClient>(MockBehavior.Strict);
                    json.Setup(b => b.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(() => Response.FromValue(BlobsModelFactory.BlobProperties(
                            contentLength: BinaryData.FromObjectAsJson(Metadata[photoId]).ToMemory().Length,
                            eTag: new ETag("\"metadata-version\"")), Mock.Of<Response>()));
                    json.Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
                        .Returns((BlobDownloadOptions options, CancellationToken _) =>
                        {
                            Assert.Equal(new ETag("\"metadata-version\""), options.Conditions.IfMatch);
                            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(
                                content: BinaryData.FromObjectAsJson(Metadata[photoId]).ToStream()), Mock.Of<Response>()));
                        });
                    Mock<BlobClient> jpeg = new Mock<BlobClient>(MockBehavior.Strict);
                    jpeg.Setup(b => b.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(() => Response.FromValue(BlobsModelFactory.BlobProperties(contentLength: SourceLength,
                            contentType: "image/jpeg", eTag: new ETag("\"photo-version\"")), Mock.Of<Response>()));
                    Blobs.Setup(b => b.GetBlobClient($"initialupload/{photoId}.json")).Returns(json.Object);
                    Blobs.Setup(b => b.GetBlobClient($"initialupload/{photoId}.jpeg")).Returns(jpeg.Object);
                }
                return new FinalizeReportRequest(photos, new ReportAttribution());
            }

            public void MastodonResponse(HttpStatusCode status, string json)
            {
                Provider.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                                                    ItExpr.Is<HttpRequestMessage>(m => m.RequestUri!.ToString() ==
                                                        "https://mastodon.example/api/v1/accounts/verify_credentials" &&
                                                        m.Headers.Authorization!.Parameter == "retained-token"),
                                                    ItExpr.IsAny<CancellationToken>())
                                                    .ReturnsAsync(() => new HttpResponseMessage(status) { Content = new StringContent(json) });
            }

            public static AuthenticateResult Auth(string did, string handle)
            {
                return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                                                [
                                                    new Claim(BlueskyAuthDefaults.DidClaim, did),
                    new Claim(BlueskyAuthDefaults.HandleClaim, handle)
                                                ], "test")), "test"));
            }

            public void Dispose()
            {
                services.Dispose();
                providerClient.Dispose();
                slackClient.Dispose();
                mapsClient.Dispose();
            }
        }
    }
}
