﻿using System.Runtime.InteropServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Azure.Cosmos.Spatial;

namespace SeattleCarsInBikeLanes
{
    public static class ExtensionMethods
    {
        public static DateTime ConvertLocalTimeOnlyToUtcDateTime(this TimeOnly time, DateOnly date)
        {
            TimeZoneInfo timeZoneInfo;
            DateTimeOffset dateTimeOffset;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            }
            else
            {
                timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
            }

            DateTime dateTime = date.ToDateTime(time, DateTimeKind.Unspecified);
            dateTimeOffset = TimeZoneInfo.ConvertTimeToUtc(dateTime, timeZoneInfo);
            return dateTimeOffset.UtcDateTime;
        }

        public static bool Contains(this BoundingBox boundingBox, Position position)
        {
            var bottomLeft = boundingBox.Min;
            var topRight = boundingBox.Max;
            return position.Latitude >= bottomLeft.Latitude &&
                position.Longitude >= bottomLeft.Longitude &&
                position.Latitude <= topRight.Latitude &&
                position.Longitude <= topRight.Longitude;
        }

        public static async Task<Uri> GenerateUserDelegationReadOnlySasUri(this BlobClient blobClient, DateTimeOffset expiresOn)
        {
            return await GenerateUserDelegationSasUri(blobClient, BlobSasPermissions.Read, expiresOn);
        }

        public static async Task<Uri> GenerateUserDelegationSasUri(this BlobClient blobClient, BlobSasPermissions blobSasPermissions, DateTimeOffset expiresOn)
        {
            BlobServiceClient serviceClient = blobClient.GetParentBlobContainerClient().GetParentBlobServiceClient();
            UserDelegationKey key = await serviceClient.GetUserDelegationKeyAsync(null, expiresOn);
            BlobSasBuilder blobSasBuilder = new BlobSasBuilder(blobSasPermissions, expiresOn)
            {
                BlobContainerName = blobClient.BlobContainerName,
                BlobName = blobClient.Name,
                Resource = "b"
            };
            return new BlobUriBuilder(blobClient.Uri)
            {
                Sas = blobSasBuilder.ToSasQueryParameters(key, serviceClient.AccountName)
            }.ToUri();
        }

        public static async Task<BlobClient> Move(this BlobClient blobClient, string newBlobName)
        {
            using MemoryStream memoryStream = new MemoryStream();
            await blobClient.DownloadToAsync(memoryStream);
            memoryStream.Seek(0, SeekOrigin.Begin);
            BlobContainerClient containerClient = blobClient.GetParentBlobContainerClient();
            BlobClient newBlobClient = containerClient.GetBlobClient(newBlobName);
            await newBlobClient.UploadAsync(memoryStream);
            await blobClient.DeleteAsync();
            return newBlobClient;
        }

        public static double DistanceTo(this Point point, Point other)
        {
            return GetDistanceTo(point.Position.Latitude, point.Position.Longitude,
                other.Position.Latitude, other.Position.Longitude);
        }

        public static double DistanceTo(this Point point, Position other)
        {
            return GetDistanceTo(point.Position.Latitude, point.Position.Longitude,
                other.Latitude, other.Longitude);
        }

        public static double DistanceTo(this Position position, Position other)
        {
            return GetDistanceTo(position.Latitude, position.Longitude,
                other.Latitude, other.Longitude);
        }

        public static double DistanceTo(this Position position, Point other)
        {
            return GetDistanceTo(position.Latitude, position.Longitude,
                other.Position.Latitude, other.Position.Longitude);
        }

        // From https://stackoverflow.com/a/51839058/6681022
        private static double GetDistanceTo(double latitude1, double longitude1, double latitude2, double longitude2)
        {
            var d1 = latitude1 * (Math.PI / 180.0);
            var num1 = longitude1 * (Math.PI / 180.0);
            var d2 = latitude2 * (Math.PI / 180.0);
            var num2 = longitude2 * (Math.PI / 180.0) - num1;
            var d3 = Math.Pow(Math.Sin((d2 - d1) / 2.0), 2.0) +
                Math.Cos(d1) * Math.Cos(d2) * Math.Pow(Math.Sin(num2 / 2.0), 2.0);

            return 6376500.0 * (2.0 * Math.Atan2(Math.Sqrt(d3), Math.Sqrt(1.0 - d3)));
        }
    }
}
