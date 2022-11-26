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
        
        public FinalizedPhotoUploadMetadata(int? numberOfCars,
            string photoId,
            DateTime photoDateTime,
            string photoLatitude,
            string photoLongitude,
            string photoCrossStreet,
            List<ImageTag> tags,
            string twitterSubmittedBy = "Submission",
            string mastodonSubmittedBy = "Submission",
            bool? attribute = null,
            string? twitterUsername = null,
            string? twitterAccessToken = null,
            string? mastodonEndpoint = null,
            string? mastodonUsername = null,
            string? mastodonFullUsername = null,
            string? mastodonAccessToken = null) : base(photoId,
                photoDateTime,
                photoLatitude,
                photoLongitude,
                photoCrossStreet,
                tags)
        {
            NumberOfCars = numberOfCars;
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
