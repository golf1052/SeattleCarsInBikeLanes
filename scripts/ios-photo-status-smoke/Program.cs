using System.Runtime.InteropServices;
using CoreGraphics;
using Foundation;
using ImageIO;
using SeattleCarsInBikeLanes.Mobile.Core.Metadata;
using SeattleCarsInBikeLanes.Mobile.Core.Models;
using SeattleCarsInBikeLanes.Platforms.iOS;
using UIKit;
using XmpCore;
using XmpCore.Options;
using ImageProperties = ImageIO.CGImageProperties;

UIApplication.Main(args, null, typeof(SmokeApp));

[Register("SmokeApp")]
public sealed class SmokeApp : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? options)
    {
        BeginInvokeOnMainThread(() =>
        {
            try
            {
                Run();
                Console.WriteLine("PHOTO_STATUS_SMOKE_PASS: all eight EXIF orientations, pixels, metadata and repeat rendering");
                Environment.Exit(0);
            }
            catch (Exception error)
            {
                Console.WriteLine($"PHOTO_STATUS_SMOKE_FAIL: {error}");
                Environment.Exit(1);
            }
        });
        return true;
    }

    private static void Run()
    {
        int[][] corners =
        [
            [0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1],
            [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]
        ];
        XmpUploadState state = new(true, DateTimeOffset.Parse("2026-09-09T00:16:48.9376486+00:00"), "smoke-receipt");
        GeoPosition location = new(47.6, -122.3);
        using UIGraphicsImageRenderer renderer = new(new CGSize(80, 60),
            new UIGraphicsImageRendererFormat { Scale = 1 });
        using UIImage image = renderer.CreateImage(context =>
        {
            UIColor[] colors = [UIColor.Red, UIColor.Green, UIColor.Blue, UIColor.Yellow];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i].SetFill();
                context.FillRect(new CGRect(i % 2 * 40, i / 2 * 30, 40, 30));
            }
        });

        for (int value = 1; value <= 8; value++)
        {
            CGImagePropertyOrientation orientation = (CGImagePropertyOrientation)value;
            using NSMutableData encoded = new();
            using (CGImageDestination destination = CGImageDestination.Create(encoded, "public.jpeg", 1)!)
            {
                using NSDictionary properties = NSDictionary.FromObjectAndKey(
                    NSNumber.FromInt32(value), ImageProperties.Orientation);
                destination.AddImage(image.CGImage!, properties);
                Require(destination.Close(), "Encode fixture");
            }
            using MemoryStream sourceBytes = new(encoded.ToArray());
            using MemoryStream captureBytes = new();
            PhotoExif.WriteCaptureMetadata(sourceBytes, captureBytes, state.UploadedAt!.Value, location);
            IXmpMeta xmp = CarsInBikeLanesXmp.Create(state);
            xmp.SetProperty("http://ns.adobe.com/tiff/1.0/", "Orientation", value.ToString());
            xmp.SetProperty("http://purl.org/dc/elements/1.1/", "source", "preserve-custom-xmp");
            byte[] original = AddXmp(captureBytes.ToArray(), xmp);
            PhotoExifData before = PhotoExif.Read(new MemoryStream(original));
            byte[] rendered = PhotoEditingRenderer.RenderUpright(original, orientation);
            if (value == 1) Require(ReferenceEquals(original, rendered), "Upright JPEG must not be recompressed");
            using NSData data = NSData.FromArray(rendered);
            using CGImageSource source = CGImageSource.FromData(data)!;
            using CGImage pixels = source.CreateImage(0, new CGImageOptions())!;
            var propertiesAfter = source.GetProperties(0, null)!;
            Require((int?)propertiesAfter.Orientation is null or 1, $"Orientation {value}: metadata must be up");
            Require(pixels.Width == (value < 5 ? 80 : 60) && pixels.Height == (value < 5 ? 60 : 80),
                $"Orientation {value}: full-resolution dimensions");
            Require(ReadCorners(pixels).SequenceEqual(corners[value - 1]), $"Orientation {value}: upright pixels");
            XmpUploadState renderedState = JpegSegmentScanner.ReadUploadState(new MemoryStream(rendered));
            Require(renderedState == state,
                $"Orientation {value}: exact receipt ID and timestamp; got {renderedState}");
            IXmpMeta afterXmp = CarsInBikeLanesXmp.TryParse(
                JpegSegmentScanner.FindXmpPacket(new MemoryStream(rendered))!)!;
            Require(afterXmp.GetPropertyString("http://purl.org/dc/elements/1.1/", "source") == "preserve-custom-xmp",
                $"Orientation {value}: custom XMP");
            Require(afterXmp.GetPropertyInteger("http://ns.adobe.com/tiff/1.0/", "Orientation") == 1,
                $"Orientation {value}: XMP orientation");
            PhotoExifData after = PhotoExif.Read(new MemoryStream(rendered));
            Require(after.TakenAt == before.TakenAt, $"Orientation {value}: capture date");
            Require(after.Location is { } actual && before.Location is { } expected &&
                Math.Abs(actual.Latitude - expected.Latitude) < 0.000001 &&
                Math.Abs(actual.Longitude - expected.Longitude) < 0.000001, $"Orientation {value}: GPS");
            Require(ReferenceEquals(rendered, PhotoEditingRenderer.RenderUpright(rendered, CGImagePropertyOrientation.Up)),
                $"Orientation {value}: retry must not rotate or recompress again");
            Console.WriteLine($"PHOTO_STATUS_SMOKE orientation {value}: PASS");
        }
    }

    private static int[] ReadCorners(CGImage image)
    {
        int width = (int)image.Width, height = (int)image.Height;
        using CGColorSpace colorSpace = CGColorSpace.CreateDeviceRGB();
        using CGBitmapContext bitmap = new(IntPtr.Zero, width, height, 8, width * 4, colorSpace,
            CGBitmapFlags.ByteOrder32Big | CGBitmapFlags.PremultipliedLast);
        bitmap.DrawImage(new CGRect(0, 0, width, height), image);
        byte[] bytes = new byte[width * height * 4];
        Marshal.Copy(bitmap.Data, bytes, 0, bytes.Length);
        return new[] { (width / 4, height / 4), (width * 3 / 4, height / 4),
            (width / 4, height * 3 / 4), (width * 3 / 4, height * 3 / 4) }
            .Select(point =>
            {
                int i = (point.Item2 * width + point.Item1) * 4;
                return bytes[i] > 180 && bytes[i + 1] > 180 ? 3 :
                    bytes[i] > 180 ? 0 : bytes[i + 1] > 180 ? 1 : 2;
            }).ToArray();
    }

    private static byte[] AddXmp(byte[] jpeg, IXmpMeta metadata)
    {
        byte[] packet = XmpMetaFactory.SerializeToBuffer(metadata, new SerializeOptions());
        byte[] signature = System.Text.Encoding.ASCII.GetBytes(JpegSegmentScanner.XmpSignature);
        using MemoryStream result = new();
        result.Write(jpeg, 0, 2);
        int length = packet.Length + signature.Length + 2;
        result.Write([0xff, 0xe1, (byte)(length >> 8), (byte)length]);
        result.Write(signature);
        result.Write(packet);
        result.Write(jpeg, 2, jpeg.Length - 2);
        return result.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
