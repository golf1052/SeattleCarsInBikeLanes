namespace SeattleCarsInBikeLanes.Storage.Models
{
    public abstract class AbstractPhotoUploadMetadata
    {
        public string PhotoId { get; set; }
        public string SubmissionId { get; set; }
        public int PhotoNumber { get; set; }
        public DateTime? PhotoDateTime { get; set; }
        public string? PhotoLatitude { get; set; }
        public string? PhotoLongitude { get; set; }
        public string? PhotoCrossStreet { get; set; }
        public List<ImageTag> Tags { get; set; }

        // Here because of bug when trying to deserialize values types to nullable value type properties
        // See https://github.com/dotnet/runtime/issues/44428
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public AbstractPhotoUploadMetadata()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        {
        }

        public AbstractPhotoUploadMetadata(string photoId,
            string submissionId,
            int photoNumber,
            DateTime photoDateTime,
            string photoLatitude,
            string photoLongitude,
            string photoCrossStreet,
            List<ImageTag> tags)
        {
            PhotoId = photoId;
            SubmissionId = submissionId;
            PhotoNumber = photoNumber;
            PhotoDateTime = photoDateTime;
            PhotoLatitude = photoLatitude;
            PhotoLongitude = photoLongitude;
            PhotoCrossStreet = photoCrossStreet;
            Tags = tags;
        }

        public AbstractPhotoUploadMetadata(string photoId,
            string submissionId,
            int photoNumber,
            List<ImageTag> tags)
        {
            PhotoId = photoId;
            SubmissionId = submissionId;
            PhotoNumber = photoNumber;
            Tags = tags;
        }

        public AbstractPhotoUploadMetadata(string photoId,
            string submissionId,
            int photoNumber,
            DateTime photoDateTime,
            string photoLatitude,
            string photoLongitude,
            List<ImageTag> tags)
        {
            PhotoId = photoId;
            SubmissionId = submissionId;
            PhotoNumber = photoNumber;
            PhotoDateTime = photoDateTime;
            PhotoLatitude = photoLatitude;
            PhotoLongitude = photoLongitude;
            Tags = tags;
        }
    }

    public class ImageTag
    {
        public string Name { get; set; } = default!;
        public float Confidence { get; set; } = default!;
    }
}
