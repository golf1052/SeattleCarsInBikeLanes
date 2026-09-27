# Publishing mobile releases

Run `scripts/release-mobile.sh` to create signed, store-ready packages for the
.NET MAUI mobile app. By default it builds both an iOS `.ipa` and an Android
`.aab`. It does **not** upload, submit, commit, tag, or push anything.

Commands below run from the repository root. The script also works from another
directory when invoked by its absolute path.

## Prerequisites

Use macOS with Bash 3.2 or newer, .NET 10, and the MAUI workloads for your selected
platforms. Install missing prerequisites yourself; the script does not install
toolchains. It uses `jq`, `xmllint`, Git, and standard macOS archive/signing tools.
An Android-only release does not require iOS distribution credentials.

For iOS, install and select a compatible Xcode, accept its license, and install
your **Apple Distribution certificate, including its private key**, and an
**App Store provisioning profile** in Xcode. Development, ad-hoc, and enterprise
profiles are not suitable. The profile must cover
`com.golf1052.SeattleCarsInBikeLanes.Mobile`. The project's signing guard uses
`/usr/bin/python3` supplied with Xcode Command Line Tools (standard library only).

For Android, install a compatible Android SDK and JDK. The script uses the .NET
Android workload's SDK/JDK discovery and bundled `bundletool.jar`. If necessary,
override the SDK locations with `AndroidSdkDirectory` and `JavaSdkDirectory` in
your local props. An explicit `BUNDLETOOL_JAR` environment variable overrides the
bundled tool; use only the [official bundletool](https://github.com/google/bundletool/releases).
The selected JDK must provide `java`, `keytool`, and `jarsigner`.

Keep the existing external `JpegXmpWritePluginMDE` project available at the
location detected by the mobile project, or set `JpegXmpWritePluginMDEProject` in
your local props. The script checks it before publishing.

## Local signing configuration

The project imports
`SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.local.props`, which is
ignored by Git. If you have not created it, copy the example:

```bash
cp SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.local.props.example \
   SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.local.props
```

**If the file already exists, merge the release property groups into it; do not
overwrite existing settings.** Fill in the example's empty values. Distribution
settings are conditional on `MobileReleasePackaging=true` and the selected
`MobileReleasePlatform`; the script supplies these properties. Ordinary Debug
and IDE Release builds keep their existing signing identities and provisioning.

| Property | Value |
| --- | --- |
| `CodesignKey` | Installed Apple Distribution identity's exact name or unique SHA1 fingerprint |
| `CodesignProvision` | Installed App Store profile name or UUID |
| `AndroidSigningKeyStore` | Absolute path to the existing Android upload/release keystore |
| `AndroidSigningKeyAlias` | Alias of the key in that keystore |
| `AndroidSigningKeyPass` | `file:/absolute/path/to/key-password` |
| `AndroidSigningStorePass` | `file:/absolute/path/to/store-password` |
| `GOOGLE_MAPS_API_KEY` | Required Android Maps SDK key; may instead come from the environment |

Use the **existing** Android upload key registered with Google Play, not a new
or debug keystore. Password references must point to nonempty, readable files;
raw passwords and `env:` password references are rejected. AAB signing does not
support the `env:` prefix used by some APK signing workflows.

Keep keystores and password files outside this repository and restrict file
permissions to your account. For example, a value in the local XML can be:

```xml
<AndroidSigningKeyPass>file:$(HOME)/.config/cars-in-bike-lanes/key-password</AndroidSigningKeyPass>
```

Do not put passwords in shell arguments, tracked files, or shared build logs.
Back up the keystore and its credentials securely. The script never creates or
replaces signing identities.

Restrict the Maps key to the app's package ID and appropriate signing certificate.
When Google Play App Signing is enabled, users receive an app signed with the
**Play app-signing certificate**, which may differ from your upload certificate;
ensure the installed store app is authorized to use Maps.

## Creating packages

```bash
./scripts/release-mobile.sh --dry-run
./scripts/release-mobile.sh
./scripts/release-mobile.sh ios
./scripts/release-mobile.sh android
```

Each successful invocation increments the build once, regardless of platform
count. A combined run uses the same version and build for both platforms.
The script preflights selected configurations, restores dependencies, builds
sequentially, and verifies each package's identity, metadata, signature, and
signer before updating version state.

| Option | Behavior |
| --- | --- |
| `both`, `ios`, `android` | Select platforms; default is `both` |
| `--bump patch` | Increment patch version; the default |
| `--bump minor` | Increment minor and reset patch to zero |
| `--bump major` | Increment major and reset minor/patch to zero |
| `--bump none` | Keep public version, increment build |
| `--version X.Y.Z` | Explicit public version, not lower than current; incompatible with `--bump` |
| `--build-number N` | Explicit positive build greater than the stored baseline |
| `--output-dir PATH` | Override the completed-release root directory |
| `--dry-run` | Preview candidate metadata/output without modifying files or requiring signing |
| `--help` | Show usage |

Dry-run does not verify toolchain/signing availability. The actual run fails with
setup guidance if required settings are absent; it never silently substitutes
development signing.

## Version and build numbers

`SeattleCarsInBikeLanes.Mobile/Version.props` is the shared source of truth.
`ApplicationDisplayVersion` is the public version (`1.0.1`).
`ApplicationVersion` is the internal build number (`4`).

| MAUI property | iOS | Android |
| --- | --- | --- |
| `ApplicationDisplayVersion` | `CFBundleShortVersionString` | `versionName` |
| `ApplicationVersion` | `CFBundleVersion` | `versionCode` |

Previously uploaded releases were iOS **1.0 build 3** and Android **1.0 build 2**.
The initial shared baseline is normalized to **1.0.0 build 3**, so the first
default invocation produces **1.0.1 build 4** on both platforms.

Public versions use three decimal components without leading zeros or suffixes.
Use patch for fixes, minor for features, and major for significant changes:

```bash
./scripts/release-mobile.sh                     # 1.0.0 (3) -> 1.0.1 (4)
./scripts/release-mobile.sh --bump minor         # 1.0.1 (4) -> 1.1.0 (5)
./scripts/release-mobile.sh ios --bump none      # 1.1.0 (5) -> 1.1.0 (6)
```

The build never resets on a version bump. Build-only mode is useful for another
TestFlight or Play internal-testing candidate of the same public version.
A new public App Store release needs a new public version, rather than only
another build of the already released version. This script does not infer
release semantics from Git commits.

Single-platform releases advance the same shared baseline. After an iOS-only
release of 1.0.1 build 4, use `android --bump none` for Android 1.0.1 build 5.
Separate packages can share the public version without identical build numbers.

Commit the updated version file yourself after a successful release and
synchronize it before releasing from another checkout. Do not revert it below
numbers already uploaded. The script never contacts the stores to discover
manual uploads; reconcile those using the explicit overrides:

```bash
./scripts/release-mobile.sh --version 1.0.1 --build-number 5
```

An override must still be higher than the stored build and any build already
uploaded for that store. Google Play's maximum build/version code is 2100000000.

## Outputs

By default, completed releases are written outside OneDrive:

```text
~/Library/Developer/SeattleCarsInBikeLanes/Releases/1.0.1-4/
    ios/       # Signed IPA and matching dSYM bundles, when generated
    android/   # Signed AAB, not the unsigned bundle
    release.json
    COMPLETE
```

The manifest records version/build, selected platforms, Git revision/dirty status,
and relative artifact paths with SHA256 checksums. Only a directory with
`COMPLETE` represents a completed release. Existing release directories are not
overwritten. iOS's archive is also retained in the standard Xcode archive
location when produced by the SDK.

Intermediate builds are isolated by run, project, platform, and runtime under
`~/Library/Caches/SeattleCarsInBikeLanes/`. This avoids sharing normal IDE AOT
caches. Successful-run scratch data is removed; failed runs retain private
diagnostic logs at the path printed by the script.

The script inherits the mobile project's automatic
[FileProvider signing guard](../SeattleCarsInBikeLanes.Mobile/IOS_SIGNING.md).
When a signing target is inside `~/Library/CloudStorage`, it suspends the current
user's FileProvider daemon only for metadata cleanup and native signing, then
resumes it, including on failure or cancellation. Do not add another manual
suspend/resume around this script: overlapping suspension owners are unsafe.
The script's normal cache outputs are outside CloudStorage, so signing there does
not suspend the daemon. Keep the per-run cache isolation even though ordinary
VSCode/CLI builds can now sign inside the synced checkout.

The repository's `publish/` directory holds this guide, not default package
outputs. Generated files there remain ignored by Git.

## Failures and retries

The version file changes **only after all selected packages succeed**.
If iOS succeeds but Android fails, the run fails overall, partial staged packages
are discarded, and the baseline remains unchanged. Fix the reported problem and
rerun the same command: it will propose the same version/build.

You may replace or rebuild **local, unuploaded** artifacts with the same version
and build. Do not reuse an uploaded build number for that store. If you manually
uploaded a partial artifact, choose a higher build explicitly before retrying.
The script cannot detect that upload.

An interrupted finalization can leave a release directory without `COMPLETE`,
with either the old or new version file. Subsequent runs using that output root
stop rather than guessing:

1. Confirm no release process is still running and inspect `release.json`,
   `Version.props`, and the store upload history.
2. Move the incomplete directory outside the output root for investigation; do
   not upload it or add `COMPLETE` by hand.
3. If neither the baseline nor any upload advanced, retry the original command.
   Otherwise, keep the higher baseline and rerun with an explicit public version
   and a build above both the stored and uploaded values. Never decrement the
   baseline to recover a build number.

The local `.mobile-release.lock/owner` file records the running PID and host.
For a stale lock after a forced termination, first confirm that process is no
longer running and that no other release is active, then remove only the lock's
`owner` file and empty lock directory. The lock serializes one checkout, not
releases made from multiple machines/clones.

## Uploading manually

For iOS, upload the completed, distribution-signed IPA using Apple's Transporter
or your existing App Store Connect workflow. Select the processed build in
TestFlight or in the matching App Store version record.

For Android, upload the completed signed AAB to the intended Google Play Console
track using your existing app listing and Play App Signing configuration.

Store processing, review, listing metadata, policy requirements, rollout, and
approval are separate from successful local packaging. Keep the release manifest
and symbols with the uploaded artifacts for crash diagnosis.
