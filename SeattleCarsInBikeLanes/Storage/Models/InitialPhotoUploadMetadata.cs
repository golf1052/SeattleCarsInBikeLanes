using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;

namespace SeattleCarsInBikeLanes.Storage.Models
{
    public class InitialPhotoUploadMetadata : AbstractPhotoUploadMetadata
    {
        public InitialPhotoUploadMetadata(string photoId,
            DateTime photoDateTime,
            string photoLatitude,
            string photoLongitude,
            string photoCrossStreet,
            List<ImageTag> tags) : base(photoId,
                photoDateTime,
                photoLatitude,
                photoLongitude,
                photoCrossStreet,
                tags)
        {
        }
    }
}
