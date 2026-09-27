# iOS signing in CloudStorage

The mobile project automatically guards iOS code signing on macOS, for both
Debug and Release, whether launched from VSCode or `dotnet`. No launch task,
output-directory override, or manual process suspension is required.

## What failed

On September 9, 2026, local device builds using .NET SDK `10.0.302`, iOS SDK
`26.5.10315`, and macOS `26.6` reproduced:

```text
Sentry.framework: resource fork, Finder information, or similar detritus not allowed
Disallowed xattr com.apple.FinderInfo found on .../Sentry.framework
```

The repository was inside `~/Library/CloudStorage/OneDrive-Personal`, but the
OneDrive application was not running. The cleanup targets were already enabled
for both Debug and Release; this was not a missing Release condition.

An instrumented Release build showed the actual sequence:

1. Recursive FinderInfo deletion returned exit code zero.
2. An immediate inspection of `Sentry.framework` confirmed FinderInfo was absent.
3. Recursive ResourceFork deletion also returned zero.
4. FinderInfo had returned before `codesign` started, and signing failed.

The second cleanup command exposed a timing window; it was not established as
the cause of the new attribute. An empty, newly created `.framework` directory
reproduced the behavior without building or copying anything. Three successful
deletions were followed by reappearance at approximately 20, 193, and 195 ms.
An equivalent directory outside CloudStorage stayed clean.

The 32-byte FinderInfo value had `20 00` at offsets 8-9: the directory's `0x2000`
package flag, not photo metadata. FileProvider metadata accompanied it.

| Experiment | Result |
| --- | --- |
| Normal Release output in OneDrive | Failed signing after cleanup |
| Same Release build with output outside CloudStorage | Full and incremental builds succeeded |
| Terminate `fileproviderd` with SIGTERM before building | macOS restarted it within about one second; signing still failed |
| Suspend with SIGSTOP, build incrementally, resume with SIGCONT | Signed successfully in about 6 seconds |
| Clean device Release first, suspend, fully rebuild, resume | AOT compilation and signing succeeded in 2 minutes 29 seconds |
| Automatic signing-only guard, without manual suspension or output override | Release compilation/AOT and signing succeeded in about 2 minutes 18 seconds |
| Automatic guard signing an intentionally invalid framework | Native signing failed, the failure propagated, and FileProvider was resumed |

The daemon stayed stopped throughout each successful suspension experiment and
was resumed immediately afterward. FinderInfo returned after resumption, but
`codesign --verify --deep --strict` still accepted the signed app. Acceptance by
the local signature verifier is not a substitute for store validation.

These experiments isolate a FileProvider-associated metadata/signing race. They
do not identify the exact thread or process issuing the attribute write.

## Why closing OneDrive is not enough

OneDrive's application and macOS's FileProvider infrastructure have separate
lifetimes. Quitting the app does not unregister its CloudStorage domain or stop
the system service. Microsoft documents local content being supplied to the sync
root even when the OneDrive application is not running.

Our machine still had the current user's `com.apple.FileProvider` launch service
running. Termination let `launchd` start it again. SIGSTOP instead leaves the
existing process alive but not executing; SIGCONT resumes it.

This is a diagnostic workaround built on documented signal semantics, **not an
Apple-supported FileProvider pause API**. It affects the user's other
FileProvider-backed cloud locations too, not only this repository.

## Automatic VSCode and CLI behavior

`SeattleCarsInBikeLanes.Mobile.csproj` selects
[`scripts/codesign-with-fileprovider-guard.py`](../scripts/codesign-with-fileprovider-guard.py)
through the iOS SDK's `CodesignPath` and `CodesignExe` properties. The native SDK
still determines identities, entitlements, signing order, and incremental work.
Signing arguments and the native tool's result are preserved.

For a signing target inside the current user's real `~/Library/CloudStorage`
directory, the guard:

1. Serializes guarded signers for this user, including overlapping builds.
2. Identifies the current FileProvider PID and verifies its executable, owner,
   and process identity. It refuses an already-suspended daemon.
3. Arms independent recovery before suspension.
4. Suspends FileProvider, removes only FinderInfo and ResourceFork from the target,
   and invokes `/usr/bin/codesign`.
5. Resumes FileProvider and releases the signing lock before returning.

Recovery also handles signing/cleanup errors, cancellation, loss of the calling
process, and a bounded timeout. The default timeout is 60 seconds per guarded
signing operation, not per entire build. `FILEPROVIDER_CODESIGN_TIMEOUT_SECONDS`
can set a limit from 1 to 300 seconds; expiry is a build error, not a successful
unguarded continuation.

The double-forked guardian is independent of the build's process group. It owns
both the suspension and a private per-user lock at
`~/Library/Caches/SeattleCarsInBikeLanes.CodesignGuard/fileprovider.lock`.
The lock file intentionally remains in place; the operating system releases its
lock when the guardian closes it. Do not delete that file while builds run.
Waiting for another signer has a separate bounded timeout using the same limit.
Critical recovery failures are also sent to the system log.

No userspace safeguard can recover if the guardian itself is forcibly killed
with SIGKILL. The protection is against normal build cancellation, wrapper/caller
death, and signing hangs, not arbitrary termination of every recovery process.

Only signing is guarded. Restore, compilation, AOT, cleaning, and deployment do
not suspend FileProvider. This narrows the successful full-build experiment to
the critical cleanup/signing window and avoids unnecessarily holding cloud
operations throughout compilation.

Targets outside CloudStorage pass through to normal signing without suspending
anything. Android, non-macOS hosts, and builds with explicit `CodesignPath` or
`CodesignExe` overrides retain their existing tool selection. The separate
pre-bundle cleanup still handles copied metadata; real cleanup errors are no
longer hidden by stderr redirection and unconditional success.

The guard uses Python 3 from Xcode Command Line Tools, with no third-party Python
packages. It does not use sudo, kill processes by name, disable services, change
sync configuration, or modify source-file metadata.

For a clean device build, from the repository root:

```bash
project="SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.csproj"
dotnet restore "$project" -r ios-arm64 -p:Configuration=Release &&
dotnet clean "$project" -f net10.0-ios -c Release -r ios-arm64 &&
dotnet build "$project" -f net10.0-ios -c Release -r ios-arm64
```

Do not separately suspend FileProvider around these commands. The guard must own
its suspension so that one caller cannot resume another caller's paused daemon.

## Opt-out and troubleshooting

Set `SuspendFileProviderDuringCodesign=false` in the ignored local props file or
on the command line to disable the guarded tool. For example, to avoid pausing
cloud services altogether, combine opt-out with nonsynced output:

```bash
dotnet build SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.csproj \
  -f net10.0-ios -c Release -r ios-arm64 \
  -p:SuspendFileProviderDuringCodesign=false \
  -p:OutputPath="$HOME/Library/Caches/SeattleCarsInBikeLanes/ios-release/"
```

An output override is still a valid alternative, but is no longer required for
the default VSCode/CLI workflow. Keep all options consistent across restore,
clean, and build when maintaining separate build profiles.

If the guard refuses a stopped daemon, do not blindly signal a PID copied from a
previous build log: it may have been reused. Determine whether another tool or
manual experiment owns the suspension first. Check the current service with
`launchctl list` and the specific process's identity and state with `ps`.

Suspension can temporarily stall other cloud-backed file accesses. A timeout or
failed recovery needs investigation; do not replace error handling with
`ContinueOnError` or `exit 0`. Moving output outside CloudStorage avoids needing
this workaround. The unrelated diagnostics/AOT startup issue is documented in
the [mobile README](README.md#ios-release-startup-troubleshooting).

The release-packaging script inherits this project integration. Its per-run
scratch outputs remain outside CloudStorage to isolate AOT caches and package
verification; those paths require no suspension. See the
[publishing guide](../publish/PUBLISHING.md).

## Regression tests

Run the guard's standard-library tests with:

```bash
/usr/bin/python3 -B scripts/tests/codesign-with-fileprovider-guard-tests.py
```

These tests use fake FileProvider state, including across real detached processes;
they never signal the real daemon. They cover timeout, cancellation, caller death,
concurrent signers, PID identity, already-stopped processes, native errors, and
resume acknowledgement. The mobile workflow runs them alongside the existing
release-script and package-verifier suites.

## Sources

- [Apple QA1940: signing rejects FinderInfo and resource forks](https://developer.apple.com/library/archive/qa/qa1940/_index.html).
- [Microsoft: OneDrive Files On-Demand on macOS](https://techcommunity.microsoft.com/blog/onedriveblog/inside-the-new-files-on-demand-experience-on-macos/3058922),
  especially "Always Keep on This Device" and package handling.
- [Apple: extension and containing-app lifetimes](https://developer.apple.com/library/archive/documentation/General/Conceptual/ExtensibilityPG/ExtensionOverview.html).
- [Apple WWDC21: FileProvider on macOS](https://developer.apple.com/videos/play/wwdc2021/10182/).
- [Apple's signal documentation](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/sigaction.2.html)
  distinguishes SIGTERM, SIGSTOP, and SIGCONT.
- [Apple-authored Finder header, mirrored](https://github.com/phracker/MacOSX-SDKs/blob/master/MacOSX11.3.sdk/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/CarbonCore.framework/Versions/A/Headers/Finder.h#L165-L173)
  documents the `0x2000` folder-package flag.
- [Apple's recursive xattr deletion implementation](https://github.com/apple-oss-distributions/file_cmds/blob/main/xattr/xattr.c#L328-L338)
  ignores absent attributes during recursive deletion, not other errors.
- [The tested iOS SDK signing target](https://github.com/dotnet/macios/blob/ac895e19154cd3305df029b18849b2e5ed98e036/msbuild/Xamarin.Shared/Xamarin.Shared.targets#L2571-L2607)
  signs embedded frameworks and the outer app through the selected tool.
