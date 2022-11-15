using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;

namespace SeattleCarsInBikeLanes.Storage.Models
{
    public class FinalizedPhotoUploadMetadata : AbstractPhotoUploadMetadata
    {
        public int? NumberOfCars { get; set; }
        public string? SubmittedBy { get; set; }
        public bool? Attribute { get; set; }
        public string? TwitterUsername { get; set; }
        public string? TwitterAccessToken { get; set; }
        
        public FinalizedPhotoUploadMetadata(int? numberOfCars,
            string photoId,
            DateTime photoDateTime,
            string photoLatitude,
            string photoLongitude,
            string photoCrossStreet,
            List<ImageTag> tags,
            string submittedBy = "Submission",
            bool? attribute = null,
            string? twitterUsername = null,
            string? twitterAccessToken = null) : base(photoId,
                photoDateTime,
                photoLatitude,
                photoLongitude,
                photoCrossStreet,
                tags)
        {
            NumberOfCars = numberOfCars;
            SubmittedBy = submittedBy;
            Attribute = attribute;
            TwitterUsername = twitterUsername;
            TwitterAccessToken = twitterAccessToken;
        }
    }
}
