using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Mobile.Core.Upload;

namespace SeattleCarsInBikeLanes.Mobile.Core.Tests;

public class ReportValidatorTests
{
    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Local);

    private static ReportDraft ValidDraft() => new ReportDraft()
    {
        NumberOfCars = 1,
        TakenAt = Now.AddHours(-1),
        Location = new GeoPosition(47.6062, -122.3321)
    };

    private static ValidationResult Validate(ReportDraft draft, int photoCount = 1) =>
        ReportValidator.Validate(draft, photoCount, BoundingBox.Seattle, maxPhotos: 4, now: Now);

    [Fact]
    public void AcceptsACompleteReport()
    {
        Assert.True(Validate(ValidDraft()).IsValid);
    }

    [Fact]
    public void RejectsAReportWithNoPhotos()
    {
        Assert.False(Validate(ValidDraft(), photoCount: 0).IsValid);
    }

    [Fact]
    public void RejectsMorePhotosThanTheServerAccepts()
    {
        ValidationResult result = Validate(ValidDraft(), photoCount: 5);

        Assert.False(result.IsValid);
        Assert.Contains("at most 4", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsFewerThanOneCar()
    {
        ReportDraft draft = ValidDraft();
        draft.NumberOfCars = 0;

        Assert.False(Validate(draft).IsValid);
    }

    [Fact]
    public void RejectsAMissingDate()
    {
        ReportDraft draft = ValidDraft();
        draft.TakenAt = null;

        Assert.False(Validate(draft).IsValid);
    }

    [Fact]
    public void RejectsAFutureDate()
    {
        ReportDraft draft = ValidDraft();
        draft.TakenAt = Now.AddMinutes(1);

        ValidationResult result = Validate(draft);

        Assert.False(result.IsValid);
        Assert.Contains("past", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAMissingLocation()
    {
        ReportDraft draft = ValidDraft();
        draft.Location = null;

        Assert.False(Validate(draft).IsValid);
    }

    [Fact]
    public void RejectsALocationOutsideSeattle()
    {
        ReportDraft draft = ValidDraft();

        // Portland.
        draft.Location = new GeoPosition(45.5152, -122.6784);

        ValidationResult result = Validate(draft);

        Assert.False(result.IsValid);
        Assert.Contains("Seattle", result.Error, StringComparison.Ordinal);
    }
}

public class BoundingBoxTests
{
    [Theory]
    [InlineData(47.6062, -122.3321, true)]   // Downtown Seattle
    [InlineData(47.495082, -122.436522, true)] // Exactly the south west corner
    [InlineData(47.735525, -122.235787, true)] // Exactly the north east corner
    [InlineData(45.5152, -122.6784, false)]  // Portland
    [InlineData(47.6101, -122.2015, false)]  // Bellevue, just east of the box
    public void ContainsMatchesTheServersBox(double latitude, double longitude, bool expected)
    {
        Assert.Equal(expected, BoundingBox.Seattle.Contains(new GeoPosition(latitude, longitude)));
    }
}
