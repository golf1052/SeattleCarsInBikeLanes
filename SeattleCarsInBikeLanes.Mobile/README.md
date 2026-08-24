# SeattleCarsInBikeLanes.Mobile

Fonts from https://github.com/microsoft/fluentui-system-icons
  - see `fonts` folder

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
purple brand color. Light and dark mode are both supported.

The app icon and splash screen remain brand purple so launch identity is stable.
The camera HUD also keeps fixed high-contrast colors over the live preview. These
Android theme resources are not loaded on iOS, so its native appearance is
unchanged. .NET MAUI 10 Shell tabs use the Material 3 token palette, but native
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
| Signed-in report | Bluesky or Mastodon sign-in is reflected in report attribution; signing out clears both HTTP and WebView sessions |
| Weak/offline network | A report stays queued, survives stopping the process, and resumes through WorkManager after connectivity returns |
| Upload payload | Large photos are resized while EXIF, GPS, orientation, and XMP remain readable by the server |
| Map | Google Maps loads and off-site main-frame links open externally without embedded posts ejecting the user from the app |

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