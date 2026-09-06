# SeattleCarsInBikeLanes.Mobile

Fonts from https://github.com/microsoft/fluentui-system-icons
  - see `fonts` folder

## App icon

The same two SVG layers in `Resources/AppIcon` define the icon on every platform:

- `appicon.svg`: pale background (`#ECF1F9`) and street grid (`#D4DFEE`).
- `appiconfg.svg`: cornflower-blue pin (`#6495ED`) and bicycle (`#142F42`).

MAUI generates Android's adaptive layers and the icons for non-iOS targets.
Android's foreground scale is `0.68` to keep the pin inside launcher masks; the
other platforms use the full-size composition.

iOS uses the standard `Platforms/iOS/Assets.xcassets/appicon.appiconset`
catalog because MAUI 10's generated `MauiIcon` does not define a dark appearance.
Its default 1024px PNG is opaque. The explicit dark PNG keeps the same foreground
and street geometry, removes the background fill, and uses `#263955` for the
streets so iOS can supply its dark backdrop. iOS chooses the appearance using the
user's Home Screen icon settings, independently of the app's in-app theme.
There is no Icon Composer document or custom build-time icon compiler.

After editing the shared SVGs, regenerate the checked-in iOS PNGs with the
.NET 10 SDK:

```bash
dotnet run --file scripts/export-ios-app-icons.cs
```

Or run the executable script directly on macOS/Linux:

```bash
./scripts/export-ios-app-icons.cs
```

These examples use the repository root, but the exporter resolves the SVGs and
output directory relative to its own source file, so it can run from any directory.
The file-based app declares its Svg.Skia and native Skia dependencies inline;
.NET restores them on the first run. It reads the shared SVGs rather than
duplicating their geometry and is an artwork-editing tool, not a build dependency.

Android 13+ has a small `mipmap-anydpi-v33/appicon.xml` override that reuses MAUI's
generated color layers and supplies `drawable/appicon_monochrome.xml` for themed
icons. This preserves the bicycle cutout and street grid: MAUI 10's default
monochrome source is the opaque colored foreground, which would hide the bicycle.
Keep this derived glyph aligned with the shared SVGs when changing the design.

Clean the affected platform target after changing icon assets to remove stale
generated resources; launchers can also cache the previous icon. For checkouts
inside OneDrive, keep signed iOS output outside the synced directory if
`codesign` reports resource-fork or Finder-info errors, for example:

```bash
dotnet build -f net10.0-ios -r iossimulator-arm64 \
  -p:OutputPath="$HOME/Library/Caches/SeattleCarsInBikeLanes/ios-build/"
```

## Android setup

The app requires a Google Maps SDK for Android API key. Copy the local build
properties template:

```bash
cp SeattleCarsInBikeLanes.Mobile.local.props.example \
  SeattleCarsInBikeLanes.Mobile.local.props
```

Put the key in `SeattleCarsInBikeLanes.Mobile.local.props`. That file is ignored
by Git and imported automatically by the project. Alternatively, set
`GOOGLE_MAPS_API_KEY` in the environment that launches the build.

Restrict the key in Google Cloud to the Android application
`com.golf1052.SeattleCarsInBikeLanes.Mobile` and the signing certificate used for that
build. The key is injected into the generated manifest and must not be committed.

Android 10 (API 29) or newer is required. Photo capture uses scoped MediaStore
storage and imported photos use the system picker, so the app does not request
broad photo or file-system access.

### Material 3

Android uses the .NET MAUI 10 Material 3 handlers and semantic design tokens.
Android 12 (API 31) and newer derive the palette from the user's wallpaper;
Android 10 and 11 use an accessible Material 3 palette generated from the app's
cornflower blue (`#6495ED`) brand color. Light and dark mode are both supported.

The shared iOS theme uses matching contrast-safe cornflower blue roles. The app
icon and splash screen use exact `#6495ED` so launch identity is stable.
The camera HUD also keeps fixed high-contrast colors over the live preview. These
Android theme resources are not loaded on iOS, which continues to use the shared
MAUI styles. .NET MAUI 10 Shell tabs use the Material 3 token palette, but native
Material 3 Shell navigation requires .NET MAUI 11.

### Deploy to an Android device

Enable USB debugging, connect an unlocked device, and verify that ADB can see it:

```bash
$HOME/Library/Android/sdk/platform-tools/adb devices -l
```

Then build and run:

```bash
dotnet build SeattleCarsInBikeLanes.Mobile.csproj \
  -t:Run \
  -f net10.0-android
```

### Android smoke test

Run this matrix on a physical Android 10+ device before merging mobile changes:

| Area | Expected result |
| --- | --- |
| First launch on iOS | Camera, photo-library, and when-in-use location permissions are requested sequentially before those features are used; denying any prompt does not crash or block unrelated startup work |
| First launch on Android | Camera and location permissions are requested sequentially; no storage permission is requested because captured photos use scoped `MediaStore` storage and imports use the system picker |
| Later launch after denial | Previously attempted permissions are status-checked but not automatically requested again; permissions granted later in system Settings are recognized |
| Camera denied | The camera preview is never created or started; the Camera tab opens directly to previous photos and the Import button remains usable |
| Photos denied or limited on iOS | Capture and import remain usable; new captures and picker copies are stored persistently inside the app and remain after process termination, device restart, and app updates, but are removed when the app is uninstalled |
| Location denied | Capture and the map picker do not re-prompt; captured photos have no GPS and submission is blocked until the user selects an in-bounds location on the map |
| Capture | Each successful shot immediately flashes the preview and produces one haptic click before its thumbnail appears; a non-black photo is saved under `Pictures/Cars in Bike Lanes`, appears in the app roll, and remains after process restart |
| Orientation and preview | The center-cropped preview fills the usable camera body in portrait and both landscape rotations without entering the status bar or display cutout; the full control rail is horizontal at the screen bottom in portrait and vertical on the physical-bottom side in landscape, the zoom pill stays next to the shutter, all controls remain clear of system insets and upright, and rotating does not restart the preview |
| Landscape capture | Photos taken in both landscape rotations display upright in the thumbnail and report preview and retain readable EXIF orientation |
| Metadata | A captured photo retains orientation, capture time, GPS when available, and the Cars in Bike Lanes XMP packet |
| Import | Up to four images can be selected with the system picker and remain readable after restarting the app |
| Thumbnails | Captured and imported images render in the roll and report preview |
| Delete | Captured photos are deleted only after app confirmation; imported photos are forgotten by the app but remain in the device library |
| Anonymous report | A report can be submitted without signing in and its photos move to the reported section |
| Report grouping on iOS and Android | A one-photo report and a separate three-photo report appear under two distinct submission-time headers, newest report first; the same groups remain after restarting the app |
| Grouped history interactions | Four-photo reports wrap within their own group; reported photos remain selectable/deletable but cannot be reported again, including in a mixed selection with unreported photos; removing the last photo removes its group, and expanded history does not crowd out recent/pending photos |
| Signed-in report | Each Settings provider button switches to Map and opens the correct sign-in modal; the other provider remains available when one account is linked; successful sign-in is reflected in Settings and report attribution |
| Sign-out synchronization | Signing out either provider in Settings updates native attribution and the already-loaded Map UI/storage; signing out from the Map website updates Settings; neither direction signs out the other provider, and Settings-originated sign-out also works when Map has not loaded yet or reloads before handling the request |
| Weak/offline network | A report stays queued, survives stopping the process, and resumes through WorkManager after connectivity returns |
| Upload payload | Large photos are resized while EXIF, GPS, orientation, and XMP remain readable by the server |
| Map | Google Maps loads and off-site main-frame links open externally without embedded posts ejecting the user from the app |

### Already reported

The camera roll groups submitted photos by their saved submission ID, with a
local submission timestamp and photo count above each report. Reports appear
newest first, even when their photos were taken earlier. The section stays
collapsed by default and scrolls within a capped height when expanded.

This is the same grouped MAUI photo list on iOS and Android, using each platform's
existing light/dark theme. Tapping still selects an individual photo for the
existing actions; it does not open a new report-details screen. Reported photos
can still be selected for deletion, but any selection containing one hides the
report button. Deselect the reported photos to report the remaining unreported
photos. Counts reflect
photos still available in the local catalog, not an account-wide report history.

To try it, expand the section with a one-photo report and a separate three-photo
report present, then restart the app and confirm the groups remain distinct.
Also check four-photo wrapping, deletion, portrait/landscape, larger text, and
light/dark mode on both platforms, including captured and imported photos.
`SiteUrls` currently targets the live site: use existing reports or a development
build pointed at a test backend rather than sending fabricated production reports.
No app-data reset or migration is needed.

### Sentry metrics parity

Use the development Sentry environment during device testing. Exercise a cold
start, leave and return to the camera tab, background and resume the app, and—on
a clean install—accept or deny the camera permission prompt. Confirm Android
samples arrive with `platform=android` for:

- `mobile.camera.ready.duration`
- `mobile.camera.permission_prompt.duration`
- `mobile.camera.ready.outcome`

The metric names, millisecond units, and `transition`, `platform`,
`permission_state`, and `result` attributes must remain identical to iOS.

## Cross-platform feature policy

Mobile features ship for iOS and Android together. Equivalent behavior and
Sentry telemetry are required on both platforms; intentional operating-system
differences must be documented here and in the pull request.

Current intentional difference: Android uses CameraX continuous autofocus
instead of the iOS tap-to-focus override because the camera toolkit does not
expose Android's `CameraControl`.

Camera controls otherwise have orientation parity. iOS interface orientation
and Android display rotation use different native conventions, so each platform
normalizes those values to the phone's physical bottom edge before the shared
camera layout places the control rail.