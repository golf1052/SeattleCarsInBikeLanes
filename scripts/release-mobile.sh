#!/bin/bash

set -euo pipefail
umask 077

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
project="$repo_root/SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.csproj"
version_file="$repo_root/SeattleCarsInBikeLanes.Mobile/Version.props"
lock_dir="$repo_root/.mobile-release.lock"
platform=both
platform_set=false
bump=patch
bump_set=false
explicit_version=
explicit_build=
dry_run=false
output_dir="$HOME/Library/Developer/SeattleCarsInBikeLanes/Releases"
work_dir=
staging_dir=
final_dir=
version_temp=
locked=false

fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "Required tool not found: $1. See publish/PUBLISHING.md."; }
usage() {
    printf '%s\n' \
        'Usage: scripts/release-mobile.sh [both|ios|android] [options]' \
        'Create signed store packages; never upload, commit, tag, or push.' \
        '' \
        '  --bump patch|minor|major|none  Version bump (default: patch); build always increases' \
        '  --version X.Y.Z               Explicit version instead of --bump' \
        '  --build-number N              Explicit build greater than the stored baseline' \
        '  --output-dir PATH             Completed release directory root' \
        '  --dry-run                     Preview without building or changing files' \
        '  --help                        Show this help'
}
cleanup() {
    status=$?
    trap - EXIT
    if [[ -n "$version_temp" ]]; then rm -f "$version_temp"; fi
    if [[ -n "$staging_dir" && -d "$staging_dir" ]]; then rm -rf "$staging_dir"; fi
    if [[ "$locked" == true ]]; then rm -f "$lock_dir/owner"; rmdir "$lock_dir"; fi
    if [[ $status -ne 0 && -n "$work_dir" ]]; then
        printf 'Private diagnostic files retained at: %s\n' "$work_dir" >&2
    fi
    if [[ $status -ne 0 && -n "$final_dir" && -d "$final_dir" && ! -f "$final_dir/COMPLETE" ]]; then
        printf 'Incomplete finalization: %s. Do not upload; see publish/PUBLISHING.md.\n' "$final_dir" >&2
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

while [[ $# -gt 0 ]]; do
    case "$1" in
        both|ios|android)
            [[ "$platform_set" == false ]] || fail 'Specify only one platform selection.'
            platform="$1"; platform_set=true; shift ;;
        --bump|--version|--build-number|--output-dir)
            [[ $# -ge 2 && -n "$2" && "$2" != --* ]] || fail "$1 requires a value."
            case "$1" in
                --bump)
                    [[ "$bump_set" == false ]] || fail 'Repeated --bump.'
                    bump="$2"; bump_set=true ;;
                --version)
                    [[ -z "$explicit_version" ]] || fail 'Repeated --version.'
                    explicit_version="$2" ;;
                --build-number)
                    [[ -z "$explicit_build" ]] || fail 'Repeated --build-number.'
                    explicit_build="$2" ;;
                --output-dir) output_dir="$2" ;;
            esac
            shift 2 ;;
        --dry-run) dry_run=true; shift ;;
        --help|-h) usage; exit 0 ;;
        *) fail "Unknown argument: $1" ;;
    esac
done
case "$bump" in patch|minor|major|none) ;; *) fail 'Invalid --bump; use patch, minor, major, or none.' ;; esac
[[ -z "$explicit_version" || "$bump_set" == false ]] || fail '--version and --bump are mutually exclusive.'

validate_version() {
    local value="$1" major minor patch
    [[ "$value" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] ||
        fail "Invalid version '$value'; expected three decimal components without leading zeros."
    IFS=. read -r major minor patch <<< "$value"
    [[ ${#major} -le 9 && ${#minor} -le 9 && ${#patch} -le 9 ]] || fail 'Version component overflow.'
}
validate_build() {
    [[ "$1" =~ ^[1-9][0-9]*$ && ${#1} -le 10 ]] || fail 'Build must be a positive decimal integer.'
    [[ "$1" -le 2100000000 ]] || fail 'Build exceeds the Google Play limit (2100000000).'
}
version_at_least() {
    local a b c x y z
    IFS=. read -r a b c <<< "$1"
    IFS=. read -r x y z <<< "$2"
    [[ $a -gt $x || ( $a -eq $x && $b -gt $y ) || ( $a -eq $x && $b -eq $y && $c -ge $z ) ]]
}

require_command xmllint
if [[ "$dry_run" == false ]]; then
    mkdir "$lock_dir" 2>/dev/null || fail "Release lock exists (or checkout is not writable): $lock_dir. See the guide before removing a stale lock."
    locked=true
    printf 'PID=%s\nHOST=%s\n' "$$" "$(hostname)" > "$lock_dir/owner"
fi
[[ -f "$version_file" ]] || fail "Missing version file: $version_file"
# Preserve trailing newlines while taking one immutable snapshot for parsing and
# the final comparison; an editor or sync client does not honor our release lock.
baseline_text="$(cat "$version_file" && printf '\001')"
baseline_text="${baseline_text%$'\001'}"
[[ "$(printf '%s' "$baseline_text" | xmllint --nonet --xpath 'count(/Project/PropertyGroup/ApplicationDisplayVersion)' -)" == 1 &&
   "$(printf '%s' "$baseline_text" | xmllint --nonet --xpath 'count(/Project/PropertyGroup/ApplicationVersion)' -)" == 1 ]] ||
    fail 'Version.props must contain exactly one display version and one build number.'
previous_version="$(printf '%s' "$baseline_text" | xmllint --nonet --xpath 'string(/Project/PropertyGroup/ApplicationDisplayVersion)' -)"
previous_build="$(printf '%s' "$baseline_text" | xmllint --nonet --xpath 'string(/Project/PropertyGroup/ApplicationVersion)' -)"
validate_version "$previous_version"
validate_build "$previous_build"

version="$explicit_version"
if [[ -z "$version" ]]; then
    IFS=. read -r major minor patch <<< "$previous_version"
    case "$bump" in
        patch) patch=$((patch + 1)) ;;
        minor) minor=$((minor + 1)); patch=0 ;;
        major) major=$((major + 1)); minor=0; patch=0 ;;
    esac
    version="$major.$minor.$patch"
fi
validate_version "$version"
version_at_least "$version" "$previous_version" || fail 'Version cannot go backwards.'
build="${explicit_build:-$((previous_build + 1))}"
validate_build "$build"
[[ $build -gt $previous_build ]] || fail 'Build must exceed the stored baseline.'
platforms=("$platform")
if [[ "$platform" == both ]]; then platforms=(ios android); fi
printf 'Release %s, build %s (%s); previous baseline %s, build %s.\n' "$version" "$build" "$platform" "$previous_version" "$previous_build"

if [[ "$dry_run" == true ]]; then
    printf 'Output: %s/%s-%s/\n' "$output_dir" "$version" "$build"
    printf 'Will create:'
    for target in "${platforms[@]}"; do
        case "$target" in ios) printf ' signed App Store IPA' ;; android) printf ' signed Play AAB' ;; esac
    done
    printf '\nNo files changed. Toolchain/signing availability is not checked in dry-run.\n'
    printf 'Required: .NET 10/selected workloads and local distribution settings; Android uses the workload bundletool (or BUNDLETOOL_JAR).\n'
    exit 0
fi

[[ "$(uname -s)" == Darwin ]] || fail 'Release packaging currently requires macOS. Dry-run is portable.'
for tool in dotnet jq git shasum unzip; do require_command "$tool"; done
[[ "$(dotnet --version)" == 10.* ]] || fail 'Select a .NET 10 SDK before packaging.'
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"
[[ -w "$output_dir" && -w "$(dirname "$version_file")" ]] || fail 'Output/version directory is not writable.'
for manifest in "$output_dir"/*/release.json; do
    [[ -f "$manifest" ]] || continue
    [[ -f "$(dirname "$manifest")/COMPLETE" ]] ||
        fail "Unfinished release at $(dirname "$manifest"). Reconcile it using publish/PUBLISHING.md before starting another release."
done
final_dir="$output_dir/$version-$build"
[[ ! -e "$final_dir" ]] || fail "Output already exists; refusing to replace it: $final_dir"
cache_root="$HOME/Library/Caches/SeattleCarsInBikeLanes"
mkdir -p "$cache_root"
work_dir="$(mktemp -d "$cache_root/release.XXXXXX")"
printf '%s' "$baseline_text" > "$work_dir/baseline.props"
staging_dir="$(mktemp -d "$output_dir/.staging-$version-$build.XXXXXX")"

property() { jq -er --arg name "$1" '.Properties[$name] | select(type == "string" and length > 0)' <<< "$properties"; }
required_property() {
    local value
    value="$(property "$1")" || fail "Set $1 in the script-scoped local release properties (see publish/PUBLISHING.md)."
    printf '%s' "$value"
}
password_file() {
    [[ "$2" == file:/* && -r "${2#file:}" && -s "${2#file:}" ]] ||
        fail "$1 must be file:/absolute/path to a nonempty, readable password file; raw/env: passwords are not supported."
}
find_ios_identity() {
    local identities line fingerprint name matches=0
    identities="$(security find-identity -v -p codesigning)" || fail 'Cannot read code signing identities from Keychain.'
    while IFS= read -r line; do
        if [[ "$line" =~ ([[:xdigit:]]{40})[[:space:]]+\"(.*)\" ]]; then
            fingerprint="${BASH_REMATCH[1]}"
            name="${BASH_REMATCH[2]}"
            if [[ "$name" == "$ios_key" || "$(printf '%s' "$fingerprint" | tr '[:lower:]' '[:upper:]')" == "$(printf '%s' "$ios_key" | tr '[:lower:]' '[:upper:]')" ]]; then
                [[ "$name" == "Apple Distribution:"* || "$name" == "iPhone Distribution:"* ]] ||
                    fail 'The selected iOS identity is not a distribution certificate.'
                ios_fingerprint="$fingerprint"
                matches=$((matches + 1))
            fi
        fi
    done <<< "$identities"
    [[ $matches -eq 1 ]] || fail 'iOS signing identity missing or ambiguous; configure its unique SHA1 fingerprint.'
}
find_ios_profile() {
    local directory profile decoded name uuid found=false
    for directory in "$HOME/Library/MobileDevice/Provisioning Profiles" "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"; do
        for profile in "$directory"/*.mobileprovision; do
            [[ -f "$profile" ]] || continue
            decoded="$work_dir/profile.plist"
            security cms -D -i "$profile" > "$decoded" 2>"$work_dir/profile.log" ||
                fail "Cannot decode installed profile: $profile"
            name="$(/usr/libexec/PlistBuddy -c 'Print :Name' "$decoded")"
            uuid="$(/usr/libexec/PlistBuddy -c 'Print :UUID' "$decoded")"
            if [[ "$name" == "$ios_profile" || "$uuid" == "$ios_profile" ]]; then found=true; break; fi
        done
        if [[ "$found" == true ]]; then break; fi
    done
    [[ "$found" == true ]] || fail 'Install the configured App Store provisioning profile in Xcode before packaging.'
}

# Query each selected target without emitting local properties or password values.
for target in "${platforms[@]}"; do
    query_args=("$project" -nologo "-p:TargetFramework=net10.0-$target" -p:Configuration=Release
        -p:MobileReleasePackaging=true "-p:MobileReleasePlatform=$target")
    if ! properties="$(dotnet msbuild "${query_args[@]}" \
        -getProperty:ApplicationId,JpegXmpWritePluginMDEProject,GOOGLE_MAPS_API_KEY,CodesignKey,CodesignProvision,AndroidSigningKeyStore,AndroidSigningKeyAlias,AndroidSigningKeyPass,AndroidSigningStorePass,AndroidSdkDirectory,JavaSdkDirectory \
        2>"$work_dir/$target-preflight.log")"; then
        fail "Cannot evaluate $target release configuration. Check the .NET 10 workload and $work_dir/$target-preflight.log."
    fi
    jq -e '.Properties | type == "object"' >/dev/null <<< "$properties" || fail 'MSBuild property query returned invalid data.'
    app_id="$(required_property ApplicationId)"
    jpeg_project="$(required_property JpegXmpWritePluginMDEProject)"
    [[ -f "$jpeg_project" ]] || fail 'The configured JpegXmpWritePluginMDE project does not exist.'
    case "$target" in
        ios)
            ios_app_id="$app_id"
            for tool in security codesign xcrun xcode-select plutil; do require_command "$tool"; done
            [[ -x /usr/bin/python3 ]] || fail 'The iOS signing guard requires /usr/bin/python3 from Xcode Command Line Tools.'
            xcrun --sdk iphoneos --show-sdk-path >"$work_dir/ios-sdk.log" 2>&1 || fail 'Select a working Xcode installation with the iPhoneOS SDK.'
            ios_key="$(required_property CodesignKey)"
            ios_profile="$(required_property CodesignProvision)"
            find_ios_identity
            find_ios_profile ;;
        android)
            android_app_id="$app_id"
            maps_key="$(required_property GOOGLE_MAPS_API_KEY)"
            unset maps_key
            android_store="$(required_property AndroidSigningKeyStore)"
            android_alias="$(required_property AndroidSigningKeyAlias)"
            android_key_pass="$(required_property AndroidSigningKeyPass)"
            android_store_pass="$(required_property AndroidSigningStorePass)"
            [[ "$android_store" == /* && -r "$android_store" ]] || fail 'AndroidSigningKeyStore must be an absolute path to your existing upload keystore.'
            password_file AndroidSigningKeyPass "$android_key_pass"
            password_file AndroidSigningStorePass "$android_store_pass"
            # SDK/JDK discovery runs in targets, not ordinary property evaluation.
            if ! dotnet restore "$project" -p:Configuration=Release -p:MobileReleasePackaging=true \
                -p:MobileReleasePlatform=android --artifacts-path "$work_dir/android/build" \
                >"$work_dir/android-restore.log" 2>&1; then
                fail "Android restore failed; see $work_dir/android-restore.log."
            fi
            if ! properties="$(dotnet msbuild "${query_args[@]}" \
                "-p:ArtifactsPath=$work_dir/android/build" -t:GetAndroidDependencies \
                -getProperty:AndroidSdkDirectory,JavaSdkDirectory,MonoAndroidToolsDirectory,AndroidBundleToolJarPath \
                2>"$work_dir/android-tooling.log")"; then
                fail "Cannot resolve the Android SDK/JDK; see $work_dir/android-tooling.log."
            fi
            android_sdk="$(required_property AndroidSdkDirectory)"
            java_sdk="$(required_property JavaSdkDirectory)"
            [[ -d "$android_sdk/platforms" && -d "$android_sdk/build-tools" ]] || fail 'Android SDK platforms/build-tools are missing.'
            [[ -d "$java_sdk/bin" ]] || fail 'The configured Java SDK is missing.'
            export PATH="$java_sdk/bin:$PATH"
            for tool in java keytool jarsigner openssl; do require_command "$tool"; done
            if [[ -z "${BUNDLETOOL_JAR:-}" ]]; then
                if BUNDLETOOL_JAR="$(property AndroidBundleToolJarPath)"; then
                    export BUNDLETOOL_JAR
                else
                    android_tools="$(required_property MonoAndroidToolsDirectory)"
                    export BUNDLETOOL_JAR="$android_tools/bundletool.jar"
                fi
            fi
            [[ -r "$BUNDLETOOL_JAR" ]] || fail 'Cannot find the workload bundletool; set BUNDLETOOL_JAR to an installed official bundletool JAR.'
            java -jar "$BUNDLETOOL_JAR" version >"$work_dir/bundletool.log" 2>&1 || fail 'BUNDLETOOL_JAR could not run with the configured JDK.'
            keytool -exportcert -keystore "$android_store" -alias "$android_alias" \
                "-storepass:file" "${android_store_pass#file:}" -file "$work_dir/upload-certificate.der" \
                >"$work_dir/android-key.log" 2>&1 || fail "Cannot open the configured upload key; see $work_dir/android-key.log."
            android_fingerprint="$(shasum -a 256 "$work_dir/upload-certificate.der")"
            android_fingerprint="${android_fingerprint%% *}" ;;
    esac
    unset properties
done

git_revision="$(git -C "$repo_root" rev-parse HEAD)"
git_dirty=false
if [[ -n "$(git -C "$repo_root" status --porcelain)" ]]; then git_dirty=true; fi
for target in "${platforms[@]}"; do
    printf 'Building %s release...\n' "$target"
    publish_dir="$work_dir/$target/publish"
    mkdir -p "$publish_dir" "$staging_dir/$target"
    args=(publish "$project" -c Release -f "net10.0-$target"
        --artifacts-path "$work_dir/$target/build" "-p:PublishDir=$publish_dir/"
        -p:MobileReleasePackaging=true "-p:MobileReleasePlatform=$target"
        "-p:ApplicationDisplayVersion=$version" "-p:ApplicationVersion=$build")
    case "$target" in
        ios)
            # The project guards codesign when its target is in CloudStorage.
            # Do not suspend FileProvider around restore, AOT, or the whole publish.
            args+=(-r ios-arm64 -p:ArchiveOnBuild=true -p:BuildIpa=true)
            pattern='*.ipa'; fingerprint="$ios_fingerprint"; app_id="$ios_app_id" ;;
        android)
            args+=(-p:AndroidKeyStore=true -p:AndroidPackageFormats=aab)
            pattern='*-signed.aab'; fingerprint="$android_fingerprint"; app_id="$android_app_id" ;;
    esac
    if ! dotnet "${args[@]}" >"$work_dir/$target-build.log" 2>&1; then
        fail "$target publish failed. Version unchanged; see $work_dir/$target-build.log."
    fi
    packages=()
    while IFS= read -r -d '' package; do packages+=("$package"); done < <(find "$publish_dir" -type f -iname "$pattern" -print0)
    [[ ${#packages[@]} -eq 1 && -s "${packages[0]}" ]] || fail "Expected exactly one nonempty $target signed package in $publish_dir."
    mkdir "$work_dir/$target/verify"
    if ! /bin/bash "$script_dir/lib/verify-mobile-package.sh" "$target" "${packages[0]}" \
        "$app_id" "$version" "$build" "$work_dir/$target/verify" "$fingerprint" \
        >"$work_dir/$target-verify.log" 2>&1; then
        fail "$target package verification failed. Version unchanged; see $work_dir/$target-verify.log."
    fi
    cp "${packages[0]}" "$staging_dir/$target/"
    if [[ "$target" == ios ]]; then
        while IFS= read -r -d '' symbol; do
            symbol_name="$(basename "$symbol")"
            [[ ! -e "$staging_dir/ios/$symbol_name" ]] || fail "Duplicate symbol bundle: $symbol_name"
            cp -R "$symbol" "$staging_dir/ios/"
        done < <(find "$work_dir/ios/build" -type d -name '*.dSYM' -prune -print0)
    fi
done

artifact_records=()
while IFS= read -r -d '' artifact; do
    digest="$(shasum -a 256 "$artifact")"
    artifact_records+=("$(jq -n --arg path "${artifact#"$staging_dir/"}" --arg sha256 "${digest%% *}" '{path:$path,sha256:$sha256}')")
done < <(find "$staging_dir" -type f -print0)
printf '%s\n' "${artifact_records[@]}" | jq -s \
    --arg version "$version" --argjson build "$build" \
    --arg previousVersion "$previous_version" --argjson previousBuild "$previous_build" \
    --arg revision "$git_revision" --argjson dirty "$git_dirty" \
    --arg platforms "${platforms[*]}" \
    '{schema:1,version:$version,build:$build,previousVersion:$previousVersion,previousBuild:$previousBuild,
      platforms:($platforms|split(" ")),gitRevision:$revision,gitDirty:$dirty,artifacts:.}' > "$staging_dir/release.json"

cmp -s "$version_file" "$work_dir/baseline.props" || fail 'Version.props changed during the build; refusing to overwrite it.'
version_temp="$(mktemp "$version_file.tmp.XXXXXX")"
printf '<Project>\n  <PropertyGroup>\n    <ApplicationDisplayVersion>%s</ApplicationDisplayVersion>\n    <ApplicationVersion>%s</ApplicationVersion>\n  </PropertyGroup>\n</Project>\n' \
    "$version" "$build" > "$version_temp"
[[ ! -e "$final_dir" ]] || fail "Output appeared during the build: $final_dir"
mv "$staging_dir" "$final_dir"
staging_dir=
mv "$version_temp" "$version_file"
version_temp=
printf '%s (%s)\n' "$version" "$build" > "$final_dir/COMPLETE"
printf 'Created release %s, build %s:\n' "$version" "$build"
for target in "${platforms[@]}"; do
    printf '  %s\n' "$final_dir/$target/"
done
printf 'Version.props updated. No packages uploaded and no Git commit created.\n'
rm -rf "$work_dir"
work_dir=
