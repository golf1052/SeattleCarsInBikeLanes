using System.Collections.ObjectModel;
using SeattleCarsInBikeLanes.Mobile.Core.Photos;

namespace SeattleCarsInBikeLanes.Mobile.ViewModels;

public sealed class ReportedPhotoGroupViewModel : ReadOnlyCollection<PhotoItemViewModel>
{
    public ReportedPhotoGroupViewModel(ReportedPhotoGroup<PhotoItemViewModel> group)
        : base(group.Photos.ToList())
    {
        SubmissionId = group.SubmissionId;
        string count = Count == 1 ? "1 photo" : $"{Count} photos";
        Header = SubmissionId is null
            ? $"Report details unavailable - {count}"
            : group.SubmittedAt is DateTimeOffset submittedAt
                ? $"Reported {submittedAt.ToLocalTime():g} - {count}"
                : $"Reported - time unavailable - {count}";
    }

    public string? SubmissionId { get; }

    public string Header { get; }
}
