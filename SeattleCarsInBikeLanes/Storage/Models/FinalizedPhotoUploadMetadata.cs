using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;

namespace SeattleCarsInBikeLanes.Storage.Models
{
    public class FinalizedPhotoUploadMetadata : AbstractPhotoUploadMetadata
    {
        public int? NumberOfCars { get; set; }
        public string? TwitterSubmittedBy { get; set; }
        public string? MastodonSubmittedBy { get; set; }
        public string? BlueskySubmittedBy { get; set; }
        public string? ThreadsSubmittedBy { get; set; }
        public bool? Attribute { get; set; }
        public string? TwitterUsername { get; set; }
        public string? TwitterAccessToken { get; set; }
        public string? MastodonEndpoint { get; set; }
        public string? MastodonUsername { get; set; }
        public string? MastodonFullUsername { get; set; }
        public string? MastodonAccessToken { get; set; }
        public string? BlueskyHandle { get; set; }
        public string? BlueskyUserDid { get; set; }
        public string? ThreadsUsername { get; set; }
        public string? ThreadsAccessToken { get; set; }
        public bool UserSpecifiedDateTime { get; set; }
        public bool UserSpecifiedLocation { get; set; }
        public string? TwitterLink { get; set; }
        public string? BlueskyAdminDid { get; set; }
        public string? BlueskyAccessJwt { get; set; }

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
            string blueskySubmittedBy = "Submission",
            string threadsSubmittedBy = "Submission",
            bool? attribute = null,
            string? twitterUsername = null,
            string? twitterAccessToken = null,
            string? mastodonEndpoint = null,
            string? mastodonUsername = null,
            string? mastodonFullUsername = null,
            string? mastodonAccessToken = null,
            string? blueskyHandle = null,
            string? blueskyUserDid = null,
            string? threadsUsername = null,
            string? threadsAccessToken = null,
            string? twitterLink = null,
            string? blueskyAdminDid = null,
            string? blueskyAccessJwt = null) : base(photoId,
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
            BlueskySubmittedBy = blueskySubmittedBy;
            ThreadsSubmittedBy = threadsSubmittedBy;
            Attribute = attribute;
            TwitterUsername = twitterUsername;
            TwitterAccessToken = twitterAccessToken;
            MastodonEndpoint = mastodonEndpoint;
            MastodonUsername = mastodonUsername;
            MastodonFullUsername = mastodonFullUsername;
            MastodonAccessToken = mastodonAccessToken;
            BlueskyHandle = blueskyHandle;
            BlueskyUserDid = blueskyUserDid;
            ThreadsUsername = threadsUsername;
            ThreadsAccessToken = threadsAccessToken;
            TwitterLink = twitterLink;
            BlueskyAdminDid = blueskyAdminDid;
            BlueskyAccessJwt = blueskyAccessJwt;
        }
    }
}
