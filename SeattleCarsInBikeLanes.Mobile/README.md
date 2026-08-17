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
`golf1052.SeattleCarsInBikeLanes.Mobile` and the signing certificate used for that
build. The key is injected into the generated manifest and must not be committed.

Android 10 (API 29) or newer is required. Photo capture uses scoped MediaStore
storage and imported photos use the system picker, so the app does not request
broad photo or file-system access.

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
| First launch | Camera permission is requested once; denying it shows an actionable message rather than crashing |
| Capture | A non-black photo is saved under `Pictures/Cars in Bike Lanes`, appears in the app roll, and remains after process restart |
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