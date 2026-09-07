using SeattleCarsInBikeLanes.Core.Contracts;

namespace SeattleCarsInBikeLanes.Storage.Models
{
    public class InitialPhotoUploadMetadata : AbstractPhotoUploadMetadata
    {
        public InitialPhotoUploadMetadata()
        {
        }

        public InitialPhotoUpload ToContract(string uri)
        {
            return new InitialPhotoUpload()
            {
                Uri = uri,
                PhotoId = PhotoId,
                SubmissionId = SubmissionId,
                PhotoNumber = PhotoNumber,
                PhotoDateTime = PhotoDateTime,
                PhotoLatitude = PhotoLatitude,
                PhotoLongitude = PhotoLongitude,
                PhotoCrossStreet = PhotoCrossStreet,
                Tags = Tags
            };
        }

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
