namespace SeattleCarsInBikeLanes.Storage.Models
{
    public class InitialPhotoUploadMetadata : AbstractPhotoUploadMetadata
    {
        public InitialPhotoUploadMetadata(string photoId,
            string submissionId,
            int photoNumber,
            DateTime photoDateTime,
            string photoLatitude,
            string photoLongitude,
            string photoCrossStreet,
            List<ImageTag> tags) : base(photoId,
                submissionId,
                photoNumber,
                photoDateTime,
                photoLatitude,
                photoLongitude,
                photoCrossStreet,
                tags)
        {
        }

        public InitialPhotoUploadMetadata(string photoId,
            string submissionId,
            int photoNumber,
            List<ImageTag> tags) : base(photoId, submissionId, photoNumber, tags)
        {
        }
    }
}
