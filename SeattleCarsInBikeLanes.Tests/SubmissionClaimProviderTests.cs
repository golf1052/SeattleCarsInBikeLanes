using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SeattleCarsInBikeLanes.Core.Contracts;
using SeattleCarsInBikeLanes.Providers;
using SeattleCarsInBikeLanes.Storage.Models;

namespace SeattleCarsInBikeLanes.Tests;

public class SubmissionClaimProviderTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(Id, true)]
    [InlineData("../report", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData(null, false)]
    public void ValidatesIds(string? id, bool valid) =>
        Assert.Equal(valid, SubmissionClaimProvider.IsValidReportId(id));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostWriteResponseAndFreshPreparationCannotReplaceAcceptedReport(bool loseResponse)
    {
        Storage storage = new Storage { FailAfterWrite = loseResponse };
        SubmissionReport first = Report("did:plc:a", 4);
        SubmissionClaimProvider service = storage.Service();
        if (loseResponse)
        {
            await Assert.ThrowsAsync<IOException>(() => service.CommitAsync(first));
        }
        else
        {
            await service.CommitAsync(first);
        }

        SubmissionReceipt retry = await storage.Service().CommitAsync(Report("did:plc:b", 1));
        Assert.Equal(first.Receipt, retry);
        SubmissionReport accepted = Assert.IsType<SubmissionReport>(await storage.Service().GetAsync(Id, "device"));
        Assert.Equal(4, accepted.Photos.Count);
        Assert.Equal(first.Photos.Select(p => p.Sha256), accepted.Photos.Select(p => p.Sha256));
        Assert.Single(await Pending(storage.Service()));
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task NoPartialReportOnFailedCreateAndNoFailOpenOnReadErrors()
    {
        Storage storage = new Storage { FailBeforeWrite = true };
        await Assert.ThrowsAsync<IOException>(() => storage.Service().CommitAsync(Report()));
        Assert.Empty(await Pending(storage.Service()));
        Assert.Null(await storage.Service().GetAsync(Id, "device"));
        storage.FailBeforeWrite = false;
        await storage.Service().CommitAsync(Report());
        storage.FailReads = true;
        await Assert.ThrowsAsync<IOException>(() => storage.Service().GetAsync(Id, "device"));
        await Assert.ThrowsAsync<IOException>(() => storage.Service().CommitAsync(Report("did:plc:b")));
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task ConcurrentFinalizationsHaveOneWinner()
    {
        Storage storage = new Storage();
        SubmissionReceipt[] receipts = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => storage.Service().CommitAsync(Report($"did:plc:{i}", 4))));
        Assert.All(receipts, receipt => Assert.Equal(receipts[0], receipt));
        Assert.Equal(1, storage.Writes);
        Assert.Single(await Pending(storage.Service()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactionRetainsReceiptAcrossFailureRestartAndLateRetry(bool loseResponse)
    {
        Storage storage = new Storage();
        SubmissionReport report = Report();
        await storage.Service().CommitAsync(report);
        storage.FailAfterWrite = loseResponse;
        if (loseResponse)
        {
            await Assert.ThrowsAsync<IOException>(() => storage.Service().RetireAsync(Id));
        }
        else
        {
            await storage.Service().RetireAsync(Id);
        }
        await storage.Service().RetireAsync(Id);
        Assert.Empty(await Pending(storage.Service()));
        SubmissionReport retired = Assert.IsType<SubmissionReport>(await storage.Service().GetAsync(Id, "device"));
        Assert.Empty(retired.Photos);
        Assert.True(retired.Retired);
        Assert.Equal(report.Receipt, await storage.Service().CommitAsync(Report("did:plc:b", 4)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Service().GetForModerationAsync(Id));
        Assert.Equal(2, storage.Writes);
    }

    [Fact]
    public async Task FailedCompactionKeepsAllBytes()
    {
        Storage storage = new Storage();
        await storage.Service().CommitAsync(Report(count: 4));
        storage.FailBeforeWrite = true;
        await Assert.ThrowsAsync<IOException>(() => storage.Service().RetireAsync(Id));
        Assert.Equal(4, (await storage.Service().GetForModerationAsync(Id)).Photos.Count);
    }

    [Fact]
    public async Task RejectsOtherDevicesInvalidPhotosAndSecrets()
    {
        Storage storage = new Storage();
        SubmissionReport report = Report();
        report.Photos[0].Metadata.MastodonAccessToken = "not-for-storage";
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.Service().CommitAsync(report));
        report.Photos[0].Metadata.MastodonAccessToken = null;
        await storage.Service().CommitAsync(report);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.Service().GetAsync(Id, "other"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.Service().CommitAsync(report with
        {
            DeviceId = "other",
            Photos = [report.Photos[0] with { Metadata = new FinalizedPhotoUploadMetadata { DeviceId = "other",
                ReportId = Id, SubmissionId = Id, PhotoId = $"{Id}-0", PhotoNumber = 0 } }]
        }));
    }

    [Fact]
    public async Task ConcurrentCompactionsCannotRestorePhotoBytes()
    {
        Storage storage = new Storage();
        await storage.Service().CommitAsync(Report());
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => storage.Service().RetireAsync(Id)));
        Assert.Empty((await storage.Service().GetAsync(Id, "device"))!.Photos);
    }

    [Fact]
    public async Task PublicationOwnershipExcludesDeletionAndSurvivesInterruptionVisibly()
    {
        Storage storage = new();
        SubmissionReport report = Report(count: 4);
        await storage.Service().CommitAsync(report);
        SubmissionReport publishing = await storage.Service().BeginModerationAsync(Id, "publishing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Service().BeginModerationAsync(Id, "deleting"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Service().RetireAsync(Id));
        SubmissionReport visible = Assert.Single(await Pending(storage.Service()));
        Assert.Equal(publishing.Moderation, visible.Moderation);
        Assert.Equal(4, visible.Photos.Count);
        Assert.Equal(report.Receipt, await storage.Service().CommitAsync(Report("did:plc:b")));
        await storage.Service().RetireAsync(Id, publishing.Moderation!.Id);
        Assert.Empty(await Pending(storage.Service()));
        Assert.Equal(report.Receipt, (await storage.Service().GetAsync(Id, "device"))!.Receipt);
    }

    [Fact]
    public async Task OnlyOwningOperationCanReleaseOrCompactAndSafeReleasePermitsRetry()
    {
        Storage storage = new();
        await storage.Service().CommitAsync(Report());
        SubmissionReport owned = await storage.Service().BeginModerationAsync(Id, "publishing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Service().ReleaseModerationAsync(Id, "other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Service().RetireAsync(Id, "other"));
        await storage.Service().ReleaseModerationAsync(Id, owned.Moderation!.Id);
        SubmissionReport deleting = await storage.Service().BeginModerationAsync(Id, "deleting");
        await storage.Service().RetireAsync(Id, deleting.Moderation!.Id);
        Assert.Empty(await Pending(storage.Service()));
    }

    internal static SubmissionReport Report(string did = "did:plc:a", int count = 1)
    {
        List<SubmissionPhoto> photos = [];
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = [0xff, 0xd8, (byte)i, 0xff, 0xd9];
            photos.Add(new SubmissionPhoto(new FinalizedPhotoUploadMetadata
            {
                PhotoId = $"{Id}-{i}",
                PhotoNumber = i,
                SubmissionId = Id,
                ReportId = Id,
                DeviceId = "device",
                BlueskyUserDid = did
            }, bytes, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return new SubmissionReport(new SubmissionReceipt(Id, Id, DateTimeOffset.UtcNow,
            new ReportAttribution(BlueskyDid: did)), "device", photos);
    }

    private static async Task<List<SubmissionReport>> Pending(SubmissionClaimProvider service)
    {
        List<SubmissionReport> result = [];
        await foreach (SubmissionReport report in service.GetPendingAsync())
        {
            result.Add(report);
        }
        return result;
    }

    internal sealed class Storage
    {
        private readonly object gate = new object();
        private byte[]? bytes;
        private int version;
        public bool FailBeforeWrite { get; set; }
        public bool FailAfterWrite { get; set; }
        public bool FailReads { get; set; }
        public int Writes => version;
        public Mock<BlobContainerClient> Container { get; } = new Mock<BlobContainerClient>();

        public Storage()
        {
            Mock<BlobClient> blob = new Mock<BlobClient>();
            Container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
            Container.Setup(c => c.GetBlobsAsync(BlobTraits.None, BlobStates.None, SubmissionClaimProvider.BlobPrefix,
                It.IsAny<CancellationToken>())).Returns(() =>
                AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(
                    bytes is null ? [] : [BlobsModelFactory.BlobItem($"{SubmissionClaimProvider.BlobPrefix}{Id}.json")],
                    null, Mock.Of<Response>())]));
            blob.Setup(b => b.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    lock (gate)
                    {
                        if (FailReads) throw new IOException("Read unavailable");
                        if (bytes is null) throw new RequestFailedException(404, "Missing");
                        return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobProperties(
                            contentLength: bytes.Length, eTag: new ETag(version.ToString())), Mock.Of<Response>()));
                    }
                });
            blob.Setup(b => b.OpenReadAsync(It.IsAny<BlobOpenReadOptions>(), It.IsAny<CancellationToken>()))
                .Returns<BlobOpenReadOptions, CancellationToken>((options, _) =>
                {
                    lock (gate)
                    {
                        if (options.Conditions?.IfMatch != new ETag(version.ToString()))
                            throw new RequestFailedException(412, "Changed");
                        return Task.FromResult<Stream>(new MemoryStream(bytes!.ToArray(), writable: false));
                    }
                });
            blob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>())).Returns<BinaryData, BlobUploadOptions, CancellationToken>(async (data, options, _) =>
                {
                    await Task.Yield();
                    lock (gate)
                    {
                        if (FailBeforeWrite) throw new IOException("Write unavailable");
                        if (options.Conditions?.IfNoneMatch == ETag.All && bytes is not null ||
                            options.Conditions?.IfMatch is ETag expected && expected != new ETag(version.ToString()))
                            throw new RequestFailedException(412, "Conditional write rejected");
                        bytes = data.ToArray();
                        version++;
                        if (FailAfterWrite)
                        {
                            FailAfterWrite = false;
                            throw new IOException("Lost response");
                        }
                        return (Response<BlobContentInfo>)null!;
                    }
                });
        }

        public SubmissionClaimProvider Service() =>
            new SubmissionClaimProvider(NullLogger<SubmissionClaimProvider>.Instance, Container.Object);
    }
}
