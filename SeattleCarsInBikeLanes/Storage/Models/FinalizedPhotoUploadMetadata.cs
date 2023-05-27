using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;

namespace SeattleCarsInBikeLanes.Storage.Models
{
    public class FinalizedPhotoUploadMetadata : AbstractPhotoUploadMetadata
    {
        public int? NumberOfCars { get; set; }
        public string? TwitterSubmittedBy { get; set; }
        public string? MastodonSubmittedBy { get; set; }
        public bool? Attribute { get; set; }
        public string? TwitterUsername { get; set; }
        public string? TwitterAccessToken { get; set; }
        public string? MastodonEndpoint { get; set; }
        public string? MastodonUsername { get; set; }
        public string? MastodonFullUsername { get; set; }
        public string? MastodonAccessToken { get; set; }
        public bool UserSpecifiedDateTime { get; set; }
        public bool UserSpecifiedLocation { get; set; }

        // Here because of bug when trying to deserialize values types to nullable value type properties
        // See https://github.com/dotnet/runtime/issues/44428
        public FinalizedPhotoUploadMetadata()
        {
        }

        public FinalizedPhotoUploadMetadata(int? numberOfCars,
            string photoId,
            string submissionId,
            int photoNumber,
            DateTime photoDateTime,
            string photoLatitude,
            string photoLongitude,
            string photoCrossStreet,
            List<ImageTag> tags,
            bool userSpecifiedDateTime,
            bool userSpecifiedLocation,
            string twitterSubmittedBy = "Submission",
            string mastodonSubmittedBy = "Submission",
            bool? attribute = null,
            string? twitterUsername = null,
            string? twitterAccessToken = null,
            string? mastodonEndpoint = null,
            string? mastodonUsername = null,
            string? mastodonFullUsername = null,
            string? mastodonAccessToken = null) : base(photoId,
                submissionId,
                photoNumber,
                photoDateTime,
                photoLatitude,
                photoLongitude,
                photoCrossStreet,
                tags)
        {
            NumberOfCars = numberOfCars;
            UserSpecifiedDateTime = userSpecifiedDateTime;
            UserSpecifiedLocation = userSpecifiedLocation;
            TwitterSubmittedBy = twitterSubmittedBy;
            MastodonSubmittedBy = mastodonSubmittedBy;
            Attribute = attribute;
            TwitterUsername = twitterUsername;
            TwitterAccessToken = twitterAccessToken;
            MastodonEndpoint = mastodonEndpoint;
            MastodonUsername = mastodonUsername;
            MastodonFullUsername = mastodonFullUsername;
            MastodonAccessToken = mastodonAccessToken;
        }
    }
}
