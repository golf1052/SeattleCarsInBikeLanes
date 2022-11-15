using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;

namespace SeattleCarsInBikeLanes.Storage.Models
{
    public abstract class AbstractPhotoUploadMetadata
    {
        public string PhotoId { get; set; }
        public DateTime PhotoDateTime { get; set; }
        public string PhotoLatitude { get; set; }
        public string PhotoLongitude { get; set; }
        public string PhotoCrossStreet { get; set; }
        public List<ImageTag> Tags { get; set; }

        public AbstractPhotoUploadMetadata(string photoId,
            DateTime photoDateTime,
            string photoLatitude,
            string photoLongitude,
            string photoCrossStreet,
            List<ImageTag> tags)
        {
            PhotoId = photoId;
            PhotoDateTime = photoDateTime;
            PhotoLatitude = photoLatitude;
            PhotoLongitude = photoLongitude;
            PhotoCrossStreet = photoCrossStreet;
            Tags = tags;
        }
    }
}
