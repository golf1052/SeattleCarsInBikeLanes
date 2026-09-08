using System.Text.Json;
using SeattleCarsInBikeLanes.Core.Contracts;

namespace SeattleCarsInBikeLanes.Core.Tests
{
    public class UploadContractSerializationTests
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions(JsonSerializerDefaults.Web);

        [Fact]
        public void InitialUploadUsesTheEstablishedWireNames()
        {
            InitialPhotoUpload upload = new InitialPhotoUpload()
            {
                Uri = "https://example.test/photo",
                PhotoId = "photo-1",
                SubmissionId = "submission-1",
                PhotoNumber = 0,
                PhotoDateTime = new DateTime(2026, 8, 18, 12, 30, 0),
                PhotoLatitude = "47.60621",
                PhotoLongitude = "-122.33207",
                PhotoCrossStreet = "Pike St",
                Tags = new List<ImageTag>()
                {
                    new ImageTag() { Name = "car", Confidence = 0.95f }
                }
            };

            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(upload, JsonOptions));
            JsonElement root = document.RootElement;

            Assert.Equal("https://example.test/photo", root.GetProperty("uri").GetString());
            Assert.Equal("photo-1", root.GetProperty("photoId").GetString());
            Assert.Equal("submission-1", root.GetProperty("submissionId").GetString());
            Assert.Equal("47.60621", root.GetProperty("photoLatitude").GetString());
            Assert.Equal("car", root.GetProperty("tags")[0].GetProperty("name").GetString());
        }

        [Fact]
        public void FinalizeRequestPreservesAllClientWritableAttributionFields()
        {
            const string json = """
            {
              "photoId": "photo-1",
              "submissionId": "submission-1",
              "photoNumber": 0,
              "numberOfCars": 2,
              "twitterUsername": "rider",
              "twitterAccessToken": "twitter-token",
              "mastodonEndpoint": "https://example.social",
              "mastodonUsername": "rider",
              "mastodonFullUsername": "@rider@example.social",
              "mastodonAccessToken": "mastodon-token",
              "threadsUsername": "rider",
              "threadsAccessToken": "threads-token",
              "deviceId": "must-not-bind"
            }
            """;

            FinalizedPhotoUpload? upload =
                JsonSerializer.Deserialize<FinalizedPhotoUpload>(json, JsonOptions);

            Assert.NotNull(upload);
            Assert.Equal("rider", upload.TwitterUsername);
            Assert.Equal("twitter-token", upload.TwitterAccessToken);
            Assert.Equal("@rider@example.social", upload.MastodonFullUsername);
            Assert.Equal("mastodon-token", upload.MastodonAccessToken);
            Assert.Equal("rider", upload.ThreadsUsername);
            Assert.Equal("threads-token", upload.ThreadsAccessToken);
            Assert.Null(typeof(FinalizedPhotoUpload).GetProperty("DeviceId"));
        }

        [Fact]
        public void DefaultLimitsMatchTheOfflineMobileFallbackWithoutAddingANullVersion()
        {
            string json = JsonSerializer.Serialize(new UploadLimits(), JsonOptions);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            Assert.Equal(4, root.GetProperty("maxPhotosPerReport").GetInt32());
            Assert.Equal(8 * 1024 * 1024, root.GetProperty("maxPhotoBytes").GetInt32());
            Assert.Equal(32 * 1024 * 1024, root.GetProperty("maxReportBytes").GetInt32());
            Assert.Equal(47.495082, root.GetProperty("southLatitude").GetDouble());
            Assert.Equal(-122.436522, root.GetProperty("westLongitude").GetDouble());
            Assert.Equal(47.735525, root.GetProperty("northLatitude").GetDouble());
            Assert.Equal(-122.235787, root.GetProperty("eastLongitude").GetDouble());
            Assert.False(root.TryGetProperty("minimumSupportedAppVersion", out _));
        }

        [Fact]
        public void BothClientsUseOneExplicitAttributionEnvelopeAndReceipt()
        {
            const string id = "0123456789abcdef0123456789abcdef";
            FinalizeReportRequest request = new FinalizeReportRequest([new FinalizedPhotoUpload()            {
                PhotoId = "prepared-photo",
                SubmissionId = "preparation",
                PhotoNumber = 0,
                NumberOfCars = 2
            }], new ReportAttribution(BlueskyDid: "did:plc:reporter"));
            string json = JsonSerializer.Serialize(request, JsonOptions);
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("prepared-photo", document.RootElement.GetProperty("photos")[0].GetProperty("photoId").GetString());
            Assert.Equal("did:plc:reporter", document.RootElement.GetProperty("attribution").GetProperty("blueskyDid").GetString());

            SubmissionReceipt receipt = new SubmissionReceipt(id, id, DateTimeOffset.UtcNow, request.Attribution);
            Assert.Equal(receipt, JsonSerializer.Deserialize<SubmissionReceipt>(JsonSerializer.Serialize(receipt, JsonOptions), JsonOptions));
            Assert.True(new ReportAttribution().IsAnonymous);
            Assert.False(request.Attribution.IsAnonymous);
        }
    }
}
