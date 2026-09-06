#!/usr/bin/env -S dotnet --
#:package Svg.Skia@5.2.3
#:package SkiaSharp.NativeAssets.Linux.NoDependencies@4.148.0
#:property PublishAot=false

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

const int iconSize = 1024;

var scriptDirectory = Path.GetDirectoryName(GetSourcePath())
    ?? throw new InvalidOperationException("Cannot locate the icon exporter.");
var root = Path.GetFullPath(Path.Combine(scriptDirectory, ".."));
var source = Path.Combine(root, "SeattleCarsInBikeLanes.Mobile", "Resources", "AppIcon");
var output = Path.Combine(root, "SeattleCarsInBikeLanes.Mobile", "Platforms", "iOS",
    "Assets.xcassets", "appicon.appiconset");

var background = XDocument.Load(Path.Combine(source, "appicon.svg"));
var darkBackground = new XDocument(background);
GetElementById(darkBackground, "background").Remove();
var streets = GetElementById(darkBackground, "streets");
var streetColor = streets.Attribute("stroke")
    ?? throw new InvalidDataException("Expected a stroke color on the streets in appicon.svg.");
streetColor.Value = "#263955";
var foreground = XDocument.Load(Path.Combine(source, "appiconfg.svg"));

using var lightSvg = new SKSvg();
using var darkSvg = new SKSvg();
using var foregroundSvg = new SKSvg();
var lightPicture = LoadPicture(lightSvg, background, "light background");
var darkPicture = LoadPicture(darkSvg, darkBackground, "dark background");
var foregroundPicture = LoadPicture(foregroundSvg, foreground, "foreground");

using var lightPng = Render(lightPicture, foregroundPicture, opaque: true);
using var darkPng = Render(darkPicture, foregroundPicture, opaque: false);
Directory.CreateDirectory(output);
Save(lightPng, "appicon-light.png", "opaque");
Save(darkPng, "appicon-dark.png", "transparent background");

static string GetSourcePath([CallerFilePath] string path = "") => path;

static XElement GetElementById(XDocument document, string id) =>
    document.Descendants().SingleOrDefault(element => (string?)element.Attribute("id") == id)
    ?? throw new InvalidDataException($"Expected an element with id='{id}' in appicon.svg.");

static SKPicture LoadPicture(SKSvg svg, XDocument document, string name) =>
    svg.FromSvg(document.ToString(SaveOptions.DisableFormatting))
    ?? throw new InvalidDataException($"Unable to render the SVG {name}.");

static SKData Render(SKPicture background, SKPicture foreground, bool opaque)
{
    using var colorSpace = SKColorSpace.CreateSrgb();
    var info = new SKImageInfo(iconSize, iconSize, SKColorType.Rgba8888,
        opaque ? SKAlphaType.Opaque : SKAlphaType.Premul, colorSpace);
    using var surface = SKSurface.Create(info)
        ?? throw new InvalidOperationException("Unable to create the icon rendering surface.");
    surface.Canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);
    DrawLayer(surface.Canvas, background);
    DrawLayer(surface.Canvas, foreground);

    using var image = surface.Snapshot();
    return image.Encode(SKEncodedImageFormat.Png, 100)
        ?? throw new InvalidOperationException("Unable to encode the icon as PNG.");
}

static void DrawLayer(SKCanvas canvas, SKPicture picture)
{
    var bounds = picture.CullRect;
    if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height)
        || bounds.Width <= 0 || bounds.Height <= 0)
    {
        throw new InvalidDataException("SVG layers must have positive, finite dimensions.");
    }

    canvas.Save();
    canvas.Scale(iconSize / bounds.Width, iconSize / bounds.Height);
    canvas.Translate(-bounds.Left, -bounds.Top);
    canvas.DrawPicture(picture);
    canvas.Restore();
}

void Save(SKData png, string filename, string appearance)
{
    using var stream = File.Create(Path.Combine(output, filename));
    png.SaveTo(stream);
    Console.WriteLine($"Exported {filename} ({iconSize}x{iconSize}, {appearance})");
}
