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
The same command also refreshes the splash images on both mobile platforms.

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

## Splash screen

On iOS and Android, the launch screen uses the full app-icon artwork and follows
the **system** light/dark setting before managed app code starts:

| System appearance | Background |
| --- | --- |
| Light | `#ECF1F9`, matching the light icon |
| Dark | `#101C2D`, the selected flat dark background |

The iOS app icon's transparent dark background is supplied by the OS; it does not
have a fixed color to reuse. The splash therefore uses the explicitly selected
dark navy. Home Screen icon appearance can be chosen independently of the system
theme and does not override the splash appearance.

iOS uses `Platforms/iOS/BikeLaunchScreen.storyboard` with `SplashBackground` and
`SplashIcon` asset-catalog appearances. Android retains MAUI's splash generation,
with native night-qualified color/image resources and `App.SplashTheme` inheriting
`Maui.SplashTheme`. Android 10/11 use the centered 128dp image; Android 12+ use
MAUI's 108dp image sizing within the OS-controlled splash mask. System-bar colors
and icon contrast follow the same appearance.

`dotnet run --file scripts/export-ios-app-icons.cs` refreshes all launch artwork
from the shared icon SVGs. There are no runtime theme-switching handlers or
artificial launch delays. The OS controls when the launch screen is shown and
dismissed; Android does not show it for hot starts, and iOS may cache launch
snapshots. Other MAUI targets retain the shared light splash fallback.

The iOS storyboard has a new name to invalidate previously cached blank launch
snapshots. If an existing device install still shows an old or blank launch screen,
restart the device after deploying rather than uninstalling and losing app data.
The splash PNGs are generated from the shared SVGs; they are native image-catalog
inputs, not a separately drawn bitmap design.

## iOS Release startup troubleshooting

If a device Release build aborts immediately, capture the failure before changing
app code. List connected devices with `xcrun devicectl list devices`, then launch
the installed app with its console attached:

```bash
xcrun devicectl device process launch --device "<device-identifier>" \
  --terminate-existing --console com.golf1052.SeattleCarsInBikeLanes.Mobile
```

A `SIGABRT` whose symbolicated native stack contains Mono's `load_aot_module` can
indicate stale or inconsistent ahead-of-time (AOT) build artifacts. This happens
before app startup and Sentry initialization, so there may be no managed exception
or Sentry event. A successful incremental build alone does not rule it out.
Device crash reports are available through `devicectl device info files` and
`devicectl device copy from` with `--domain-type systemCrashLogs`; keep the matching
build's `.app.dSYM` to symbolicate them.

For a connected iPhone, clean both the Release configuration and the physical-device
runtime. From the `SeattleCarsInBikeLanes.Mobile` directory:

```bash
dotnet clean -c Release -f net10.0-ios -r ios-arm64
```

Plain `dotnet clean` defaults to Debug and leaves Release artifacts untouched.
Adding only `-c Release` is not enough: without `-r ios-arm64`, the iOS SDK on an
Apple Silicon Mac still selects `iossimulator-arm64`, even with an iPhone connected.

Clean does not restore packages. If the latest build restored only `ios-arm64`,
a simulator clean fails with `NETSDK1047` because `obj/project.assets.json` has no
simulator target. This is a mismatch between the clean command and generated
restore state, not a missing iOS framework in the `.csproj`. If the device target
itself is missing, run `dotnet restore -r ios-arm64 -p:Configuration=Release` before
the device clean.

Clean the exact device configuration, then rebuild. From the repository root:

```bash
project="SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.csproj"
output="$HOME/Library/Caches/SeattleCarsInBikeLanes/ios-release/"

dotnet restore "$project" -r ios-arm64 -p:Configuration=Release \
  -p:OutputPath="$output" &&
dotnet clean "$project" -f net10.0-ios -c Release -r ios-arm64 \
  -p:OutputPath="$output" &&
dotnet build "$project" -f net10.0-ios -c Release -r ios-arm64 \
  -p:OutputPath="$output"
```

The output override keeps the signed bundle outside OneDrive. Otherwise, OneDrive
can attach `com.apple.FinderInfo` to a generated framework such as `Sentry.framework`
and make code signing fail even after the project's metadata-cleanup targets run.
That signing failure is separate from the runtime AOT abort.

Install the newly built bundle over the existing app, then relaunch:

```bash
xcrun devicectl device install app --device "<device-identifier>" \
  "$HOME/Library/Caches/SeattleCarsInBikeLanes/ios-release/SeattleCarsInBikeLanes.Mobile.app"
xcrun devicectl device process launch --device "<device-identifier>" \
  --terminate-existing --console com.golf1052.SeattleCarsInBikeLanes.Mobile
```

Do not uninstall or reset app data for this recovery: an over-install preserves
private photos, settings, and the upload queue. Deploy the bundle from the selected
output directory, not an older copy under `bin`.

### Confirmed diagnostics-toggle trigger

With .NET SDK `10.0.302` and iOS workload `26.5.10315`, this failure was reproduced
before the project override below by building Release normally, then building the
same configuration/RID/output incrementally with `-p:EnableDiagnostics=True`.
No source or package change was required. VS Code's MAUI extension `1.16.88` adds
this property when its XAML diagnostics are enabled, including for the Release
launch configuration.

Enabling diagnostics changes the EventSource/Metrics feature switches used during
trimming. That produces different `System.Private.CoreLib` and
`System.Collections.Concurrent` assembly identities (MVIDs). The iOS SDK can retain
AOT output for unchanged dependent assemblies instead of rebuilding it against
those new identities. In the reproduced failure, `SeattleCarsInBikeLanes.Core` and
four `SQLitePCLRaw` modules retained stale references. Mono reported:

```text
AOT: module SeattleCarsInBikeLanes.Core is unusable
(GUID of dependent assembly System.Private.CoreLib doesn't match ...)
```

Regenerating those five native AOT modules, with the same diagnostics settings and
without changing managed code, repaired the bundle. This was reproduced with
signed output outside OneDrive; Finder metadata is a separate signing problem.
See the SDK's [diagnostics-dependent trimming defaults](https://github.com/dotnet/macios/blob/ac895e19154cd3305df029b18849b2e5ed98e036/dotnet/targets/Xamarin.Shared.Sdk.targets#L133-L139)
and [AOT dependency freshness logic](https://github.com/dotnet/macios/blob/ac895e19154cd3305df029b18849b2e5ed98e036/msbuild/Xamarin.MacDev.Tasks/Tasks/AOTCompile.cs#L94-L165).

The mobile project now forces `EnableDiagnostics` and `EnableMauiXamlDiagnostics`
to `false` for iOS Release only. Its `TreatAsLocalProperty` declaration allows those
values to override VS Code's command-line properties; ordinary project properties
cannot override `-p:` values. Debug retains its diagnostics and XAML Hot Reload,
and Android settings are unchanged. No global VS Code Hot Reload setting is needed.

Clean the iOS Release configuration once when adopting this override to remove
previous diagnostics-enabled AOT output. Later command-line and IDE Release builds
use consistent diagnostics settings without cleaning on every build. If deliberately
maintaining separate diagnostic and distribution profiles, isolate both intermediate
and output directories: changing only `OutputPath` still shares the AOT cache under
`obj`. Disabling trimming or enabling the interpreter is not necessary.

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

The shared iOS theme uses matching contrast-safe cornflower blue roles. The pin
in the app icon and splash screen uses exact `#6495ED`, while the splash background
follows the light/dark colors above.
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
| Photos denied or limited on iOS | Capture and import remain usable; new captures and picker copies are stored persistently inside the app and remain after process termination, normal device restart, and app updates, but are removed when the app is uninstalled; see the file-persistence limitation below |
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

### Durable submission and recovery

For app-owned captures and private photos (including private imported copies), embedded
XMP in the current rendition is the durable submission history: uploaded flag,
canonical submission ID, and server submission timestamp. Read-only imported library
references keep their existing submitted-state index; the app never rewrites their
originals. Explicitly picking an older owned capture retains a reference in the roll,
but does not make that index authoritative for its submission state.

The queue persists a server receipt before local acknowledgement. A report labelled
**Sent; saving photo status** has already reached the server. Retry finishes only its
XMP/index acknowledgement; it does not submit again or require an account. Its photos
remain reserved until every acknowledgement is persisted and verified; these confirmed
reports cannot be cancelled. Retrying an uncertain network outcome first reconciles
the existing report ID rather than creating a new independent report.

Failed uploads without a confirmed receipt offer **Retry** and **Cancel**, including
old uploads whose API is no longer available. Cancelling requires confirmation, removes
the saved queue entry and error, releases queued credentials, and keeps the photos
available to report again. It works offline and remains cancelled after restarting
the app. If a network attempt was made, the confirmation warns that the server may
already have received the report: cancelling is local only, does not remove anything
from the site, and reporting those photos again could create a duplicate.

iOS stamps the rendered current photo rather than reconstructing earlier adjustment
recipes, and reads the edited resource back. Android keeps the MediaStore asset ID
unchanged: before truncating, it saves and flushes the contents of original/staged
JPEGs and a journal in the app's no-backup directory, then publishes the journal.
Process-interrupted edits are recovered before app access when those files remain
available, subject to the file-persistence limitation below.
Even matching recovered bytes are synchronized before journal/backup retirement.
Recovery conflicts or unavailable targets are quarantined, keeping their recovery
copies and queue operation without blocking unrelated photos. Do not uninstall or
clear app data to troubleshoot recovery: that destroys those private recovery copies.
The in-place MediaStore write is not atomic to other applications; an external edit
or reused/deleted URI is not overwritten blindly.

### File persistence and the accepted directory-durability gap

`DurableFile`, private photo storage, photo recovery backups/journals, and pending
browser sign-out records use standard .NET file APIs. File contents are flushed with
`FileStream.Flush(flushToDisk: true)` before publishing temporary files or starting
an in-place photo mutation. Temporary-file replacement, backup/journal ordering,
photo readback verification, and propagation of storage failures are retained.

Flushing file contents does not necessarily persist the parent directory's creation,
rename, or deletion, including changes to its file entries. The standard .NET APIs
used here do not explicitly flush directory metadata. Following an abrupt OS crash
or power loss, recent directory changes may therefore be lost even after file
contents were flushed. A new photo, metadata replacement, recovery file/journal,
or sign-out record may disappear or revert; a deletion may not persist.

Atomic replacement and durable persistence are different guarantees: replacing a
file without exposing a partial write does not ensure the replacement survives
power loss. The user deliberately accepts this residual edge case in exchange for
using standard .NET APIs. This is **not complete power-loss durability**. Recovery
still protects interrupted in-place edits when the recovery files are available;
it cannot guarantee recovery if an OS crash loses their directory entries. Native
PhotoKit and MediaStore operations are unchanged.

Host tests cover service recreation, process-interruption simulations, ordering,
readback, and I/O failures. They do not simulate an OS crash or power loss or prove
directory durability.

### Queued attribution and sign-out

A report freezes the account/provider selection displayed at submission. Credentials
for already-queued reports are intentionally retained in secure storage after active
sign-out or switching accounts. They cannot sign the old account back into Settings
or the website. New reports use the current identity; queued reports never adopt a
newly linked account.

If retained credentials expire or are revoked before acceptance, the entire report
automatically falls back to anonymous, without a prompt or reauthentication.
Temporary verification failures retry instead. Native uploads use explicit queued
credentials on a cookie-free, non-redirecting client; anonymous finalization strips
all attribution even if unrelated authentication is present. Once accepted, the
server's original attribution is preserved through recovery.

The secure vault stores snapshots and their report references together. Successful
receipt persistence, anonymous fallback, safe discard, and failed enqueue release
unneeded references. Startup reconciles orphan references only after loading the
durable queue successfully; cleanup failures are logged and retried on restart.
Queue JSON and pending browser-clear records contain no tokens.

Provider sign-out is persisted before active credentials are cleared. Browser clear
actions survive process restart (subject to the file-persistence limitation above)
and are acknowledged only after native sign-out completes.
A durable signed-out flag blocks later stale browser capture until an explicit
sign-in action. Bluesky Settings validates the native bearer, independently of web
cookies; transient failures retain the saved account. These bearers are this site's
protected identity tickets, not live Bluesky OAuth refresh tokens.

The app and website share `POST /api/Upload/Initial`, `POST /api/Upload/Finalize`,
and `GET /api/Upload/Reports/{reportId}`. Both upload stages send the queue's stable
`X-Report-Id` and installation `X-Device-Id`; finalization sends a
`FinalizeReportRequest` with ordered photos and explicit queued attribution.
All photos share the report's date, location, cross street, and car count.
Typed `report_in_progress` responses honor `Retry-After` and reconcile the receipt;
`preparation_expired` responses retry with fresh preparation under the same report
ID. Device blocking and identity mismatches remain visible failures, not anonymous
fallbacks. This requires the shared server API from PR #21, not `FinalizeMobile`.
Queue schema v2 and active-session v2 do not migrate old author-only prerelease
queue/login state. Imported-library references are preserved.

Host fault-injection tests and Release compilation do not replace native PhotoKit,
MediaStore/power-loss, locked-device secure storage, or WebView lifecycle exercise.
Use the smoke matrix above with a test backend before release. No production test
uploads are needed for automated coverage.

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

## In-app iOS Camera Control

On iOS 18+ devices with Camera Control, a full click takes one photo on release.
A light press opens Apple's native zoom control; slide on Camera Control to zoom.
Volume buttons also take photos while the hardware interaction is enabled.
There is no burst/video gesture or exposure control.

The app does not register as a Camera Control launch target: there is no
`CameraCaptureIntent`, Lock Screen camera extension, or launch-setting change.
Open the app normally. Hardware controls are enabled only while the live camera
preview is ready and visible, and are disabled during capture, camera switching,
photo importing, photo-roll viewing, navigation away, and app inactivity.
Disabled interactions leave hardware buttons to their normal system behavior.
Inactive zoom controls are removed from the capture session and registered again
when the preview is ready, rather than toggling `AVCaptureSlider.Enabled`. This
avoids the faint, unresponsive overlay observed after returning from the photo
roll or resuming the app. Pending actions from an earlier registration are ignored.

Hardware zoom, pinch zoom, and the zoom pill share the active camera format's
system-recommended range, constrained by current native device availability.
This range is not subject to the app's usual 10x digital-zoom cap. The displayed
zoom follows native changes, and reopening or switching cameras resets to the
default zoom. The native overlay uses a configurable `AVCaptureSlider` so pinch
zoom and the zoom pill also update Camera Control's displayed value. Hardware
scrolling uses 0.25x steps (or the full range if narrower) to avoid excessive
scrolling. Pinch zoom remains continuous and its exact value is reflected in the
overlay without rounding to those steps. Hardware swipes update the same device
zoom; queued programmatic value-change callbacks
do not replay earlier zoom levels. The slider is rebuilt when its range changes.
A missing usable native recommendation or unavailable zoom control
retains the existing touch-zoom policy without disabling hardware shutter support.
Unsupported iPhones and Android retain the existing touch controls and zoom range.

Physical-device acceptance requires an iPhone with Camera Control. Open the app
while a different camera app is selected as the system launch target; exercise
click capture, light-press/swipe zoom, alternating pinch/pill/hardware zoom,
rear/front switching, both landscape orientations, and repeated roll/tab/app
transitions. Check that no capture occurs behind the roll or an import sheet, and
that leaving immediately after a shutter press saves exactly one photo. A
simulator cannot validate these physical gestures.
Test the photo-roll and background/foreground transitions separately: zoom must
still move both the native overlay and preview after each, including when pinch
zoom leaves the device between the hardware slider's quarter-step values.

## Cross-platform feature policy

Mobile features ship for iOS and Android together. Equivalent behavior and
Sentry telemetry are required on both platforms; intentional operating-system
differences must be documented here and in the pull request.

Current intentional difference: Android uses CameraX continuous autofocus
instead of the iOS tap-to-focus override because the camera toolkit does not
expose Android's `CameraControl`.

Camera Control is an intentional iOS-only hardware integration. Supported iPhones
also use Apple's recommended zoom range for all zoom inputs; Android and
unsupported iPhones keep the existing device-limited, 10x-capped zoom policy.

On-screen camera controls otherwise have orientation parity. iOS interface orientation
and Android display rotation use different native conventions, so each platform
normalizes those values to the phone's physical bottom edge before the shared
camera layout places the control rail.