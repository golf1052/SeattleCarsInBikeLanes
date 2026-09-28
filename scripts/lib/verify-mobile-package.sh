#!/bin/bash
# Verify the built artifact, not the MSBuild inputs. Keep diagnostics in caller-owned scratch.
set -euo pipefail
export LC_ALL=C
export LANG=C
umask 077

fail() {
    printf 'Package verification failed: %s\n' "$*" >&2
    exit 1
}

require_tool() {
    command -v "$1" >/dev/null 2>&1 || fail "Required tool not found: $1"
}

normalize_fingerprint() {
    printf '%s' "$1" | tr -d ':' | tr '[:lower:]' '[:upper:]'
}

expect_equal() {
    [[ "$2" == "$3" ]] || fail "$1 does not match the requested release."
}

[[ $# -eq 7 ]] || fail \
    'Usage: verify-mobile-package.sh PLATFORM PACKAGE APP_ID VERSION BUILD SCRATCH_DIRECTORY EXPECTED_SIGNER_FINGERPRINT'
platform=$1
package=$2
app_id=$3
version=$4
build=$5
scratch=$6
expected_signer=$7

case "$platform" in
    ios|android) ;;
    *) fail "Platform must be ios or android." ;;
esac
[[ -n "$app_id" && -n "$version" && -n "$build" ]] || fail "Release metadata must not be empty."
[[ -f "$package" && -s "$package" ]] || fail "Package is missing, empty, or not a regular file."
[[ -d "$scratch" && ! -L "$scratch" ]] || fail "Scratch must be an existing, dedicated directory, not a symlink."
scratch=$(cd -P -- "$scratch" && pwd) || fail "Cannot resolve scratch directory."
script_directory=$(cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd) || fail "Cannot resolve helper directory."
checkout=$(cd -- "$script_directory/../.." && pwd -P) || fail "Cannot resolve checkout directory."
case "$scratch/" in
    "$checkout/"*) fail "Verification scratch must be outside the checkout." ;;
esac
shopt -s nullglob dotglob
scratch_entries=("$scratch"/*)
[[ ${#scratch_entries[@]} -eq 0 ]] || fail "Scratch directory must be empty; existing files will not be overwritten."
shopt -u dotglob

for tool in unzip zipinfo awk grep tr; do
    require_tool "$tool"
done
expected_signer=$(normalize_fingerprint "$expected_signer") || fail "Cannot normalize expected signer fingerprint."
case "$platform" in
    ios) [[ "$expected_signer" =~ ^[0-9A-F]{40}$ ]] || fail "iOS requires a SHA1 certificate fingerprint (40 hexadecimal digits)." ;;
    android) [[ "$expected_signer" =~ ^[0-9A-F]{64}$ ]] || fail "Android requires a SHA256 certificate fingerprint (64 hexadecimal digits)." ;;
esac
package_directory=$(cd -P -- "$(dirname -- "$package")" && pwd) || fail "Cannot resolve package directory."
package="$package_directory/$(basename -- "$package")"

# Reject unsafe names and links before any extraction. iOS bundles do not need
# macOS-style versioned-framework symlinks, so fail closed on all ZIP symlinks.
zipinfo -1 "$package" > "$scratch/zip-entries.txt" 2> "$scratch/zip-list.log" ||
    fail "Cannot list package ZIP entries; see scratch/zip-list.log."
[[ -s "$scratch/zip-entries.txt" ]] || fail "Package ZIP is empty."
while IFS= read -r entry || [[ -n "$entry" ]]; do
    [[ -n "$entry" && ! "$entry" =~ [[:cntrl:]] ]] || fail "ZIP contains an empty or control-character entry name."
    case "$entry" in
        /*|\\*|[A-Za-z]:*|*\\*|*^*|..|../*|*/../*|*/..|.|./*|*/./*|*/.|*//*)
            fail "ZIP contains an unsafe path."
            ;;
    esac
done < "$scratch/zip-entries.txt"
awk '
    { name = $0; sub(/\/$/, "", name); if (seen[name]++) exit 1 }
' "$scratch/zip-entries.txt" || fail "ZIP contains duplicate paths."
zipinfo -l "$package" > "$scratch/zip-attributes.txt" 2>> "$scratch/zip-list.log" ||
    fail "Cannot inspect ZIP entry attributes."
awk '
    /^[bclps]/ { exit 1 }
' "$scratch/zip-attributes.txt" || fail "ZIP contains a symlink or special file."
unzip -tqq "$package" > "$scratch/zip-test.log" 2>&1 ||
    fail "ZIP integrity check failed; see scratch/zip-test.log."

verify_android() {
    local manifest actual_id actual_version actual_build status actual_signer
    for tool in java jarsigner keytool xmllint; do
        require_tool "$tool"
    done
    [[ -n "${BUNDLETOOL_JAR:-}" && -f "$BUNDLETOOL_JAR" && -s "$BUNDLETOOL_JAR" ]] ||
        fail "Set BUNDLETOOL_JAR to an existing official bundletool JAR; no tools are downloaded."
    java -Duser.language=en -Duser.country=US -jar "$BUNDLETOOL_JAR" validate "--bundle=$package" \
        > "$scratch/bundletool-validate.log" 2>&1 ||
        fail "bundletool validate failed; see scratch/bundletool-validate.log."
    manifest="$scratch/AndroidManifest.xml"
    java -Duser.language=en -Duser.country=US -jar "$BUNDLETOOL_JAR" dump manifest \
        "--bundle=$package" --module=base > "$manifest" 2> "$scratch/bundletool-manifest.log" ||
        fail "bundletool manifest inspection failed; see scratch/bundletool-manifest.log."
    xmllint --nonet --noout "$manifest" 2> "$scratch/manifest-xml.log" ||
        fail "bundletool did not produce a valid XML manifest."
    actual_id=$(xmllint --nonet --xpath 'string(/manifest/@package)' "$manifest" 2>> "$scratch/manifest-xml.log") ||
        fail "Cannot read Android package identifier."
    actual_version=$(xmllint --nonet --xpath \
        'string(/manifest/@*[local-name()="versionName" and namespace-uri()="http://schemas.android.com/apk/res/android"])' \
        "$manifest" 2>> "$scratch/manifest-xml.log") || fail "Cannot read Android versionName."
    actual_build=$(xmllint --nonet --xpath \
        'string(/manifest/@*[local-name()="versionCode" and namespace-uri()="http://schemas.android.com/apk/res/android"])' \
        "$manifest" 2>> "$scratch/manifest-xml.log") || fail "Cannot read Android versionCode."
    expect_equal "Android package identifier" "$actual_id" "$app_id"
    expect_equal "Android versionName" "$actual_version" "$version"
    expect_equal "Android versionCode" "$actual_build" "$build"

    # -strict encodes unsigned entries in exit bit 16. Exit 4 also covers the
    # untrusted/self-signed upload certificates that Google Play normally uses.
    if jarsigner -J-Duser.language=en -J-Duser.country=US -verify -strict -verbose:all "$package" \
        > "$scratch/jarsigner.log" 2>&1; then
        status=0
    else
        status=$?
    fi
    case "$status" in
        0|4) ;;
        *) fail "JAR signature verification failed (status $status); see scratch/jarsigner.log." ;;
    esac
    grep -Eq '^jar verified[.]$|^jar verified, with signer errors[.]$' "$scratch/jarsigner.log" ||
        fail "Package is unsigned or its JAR signature could not be verified."
    grep -Eq '^s[m k]*[[:space:]]+[0-9]+[[:space:]]' "$scratch/jarsigner.log" ||
        fail "JAR verification did not confirm any signed entries."
    awk '
        tolower($0) ~ /unsigned entries|jar is unsigned|certificate has expired|certificate is not yet valid/ { exit 1 }
    ' "$scratch/jarsigner.log" ||
        fail "Package contains unsigned entries or an invalid signing certificate; see scratch/jarsigner.log."
    keytool -J-Duser.language=en -J-Duser.country=US -printcert -jarfile "$package" \
        > "$scratch/android-certificate.log" 2>&1 ||
        fail "Cannot inspect the Android signing certificate."
    actual_signer=$(awk '
        /^Signer #[0-9]+:$/ { signers++; leaf = 0 }
        /^Certificate #[0-9]+:$/ { leaf = ($0 == "Certificate #1:") }
        leaf && /^[[:space:]]*SHA256:/ { fingerprints++; fingerprint = $2 }
        END {
            if (signers != 1 || fingerprints != 1) exit 1
            print fingerprint
        }
    ' "$scratch/android-certificate.log") ||
        fail "Android package must have exactly one identifiable signing leaf certificate."
    actual_signer=$(normalize_fingerprint "$actual_signer") || fail "Cannot normalize Android certificate fingerprint."
    [[ "$actual_signer" =~ ^[0-9A-F]{64}$ ]] || fail "Android signing certificate SHA256 is malformed."
    expect_equal "Android signing certificate" "$actual_signer" "$expected_signer"
}

plist_value() {
    local file=$1 key=$2 type=$3
    plutil -extract "$key" raw -expect "$type" -o - "$file" 2>> "$scratch/plist.log"
}

verify_ios() {
    local app info actual_id actual_version actual_build actual_signer profile profile_xml
    local profile_id team app_id_prefix get_task_allow expiration now forbidden_count root_count
    local apps
    for tool in codesign security plutil openssl xmllint date; do
        require_tool "$tool"
    done
    mkdir "$scratch/ipa" || fail "Cannot create IPA extraction directory."
    unzip -q "$package" -d "$scratch/ipa" > "$scratch/ipa-extract.log" 2>&1 ||
        fail "IPA extraction failed; see scratch/ipa-extract.log."
    shopt -s dotglob
    apps=("$scratch/ipa/Payload/"*.app)
    shopt -u dotglob
    [[ ${#apps[@]} -eq 1 && -d "${apps[0]}" && ! -L "${apps[0]}" ]] ||
        fail "IPA must contain exactly one Payload/*.app directory."
    app=${apps[0]}
    info="$app/Info.plist"
    [[ -f "$info" && ! -L "$info" ]] || fail "IPA application is missing Info.plist."
    plutil -lint "$info" > "$scratch/plist.log" 2>&1 || fail "Application Info.plist is malformed."
    actual_id=$(plist_value "$info" CFBundleIdentifier string) || fail "Cannot read CFBundleIdentifier."
    actual_version=$(plist_value "$info" CFBundleShortVersionString string) || fail "Cannot read CFBundleShortVersionString."
    actual_build=$(plist_value "$info" CFBundleVersion string) || fail "Cannot read CFBundleVersion."
    expect_equal "iOS bundle identifier" "$actual_id" "$app_id"
    expect_equal "iOS CFBundleShortVersionString" "$actual_version" "$version"
    expect_equal "iOS CFBundleVersion" "$actual_build" "$build"

    codesign --verify --deep --strict "$app" > "$scratch/codesign-verify.log" 2>&1 ||
        fail "iOS code signature verification failed; see scratch/codesign-verify.log."
    codesign -d --extract-certificates="$scratch/ios-signing-cert-" "$app" \
        > "$scratch/codesign-certificate.log" 2>&1 ||
        fail "Cannot extract the iOS signing certificate."
    [[ -s "$scratch/ios-signing-cert-0" ]] || fail "iOS package has no signing leaf certificate (possibly ad-hoc signed)."
    actual_signer=$(openssl x509 -inform DER -in "$scratch/ios-signing-cert-0" -noout -fingerprint -sha1 \
        2> "$scratch/ios-certificate.log") || fail "Cannot read iOS signing certificate SHA1."
    actual_signer=${actual_signer#*=}
    actual_signer=$(normalize_fingerprint "$actual_signer") || fail "Cannot normalize iOS certificate fingerprint."
    [[ "$actual_signer" =~ ^[0-9A-F]{40}$ ]] || fail "iOS signing certificate SHA1 is malformed."
    expect_equal "iOS signing certificate" "$actual_signer" "$expected_signer"

    [[ -s "$app/embedded.mobileprovision" && ! -L "$app/embedded.mobileprovision" ]] ||
        fail "IPA is missing its embedded App Store provisioning profile."
    profile="$scratch/profile.plist"
    security cms -D -i "$app/embedded.mobileprovision" -o "$profile" \
        > "$scratch/profile-cms.log" 2>&1 ||
        fail "Cannot decode the embedded provisioning profile; see scratch/profile-cms.log."
    plutil -lint "$profile" >> "$scratch/plist.log" 2>&1 ||
        fail "Embedded provisioning profile is malformed."
    profile_xml="$scratch/profile.xml"
    plutil -convert xml1 -o "$profile_xml" "$profile" >> "$scratch/plist.log" 2>&1 ||
        fail "Cannot convert embedded provisioning profile to XML."
    root_count=$(xmllint --nonet --xpath 'count(/plist/dict)' "$profile_xml" 2> "$scratch/profile-xml.log") ||
        fail "Cannot inspect provisioning profile structure."
    [[ "$root_count" == 1 ]] || fail "Provisioning profile must be a plist dictionary."
    forbidden_count=$(xmllint --nonet --xpath \
        'count(/plist/dict/key[.="ProvisionedDevices" or .="ProvisionsAllDevices"])' \
        "$profile_xml" 2>> "$scratch/profile-xml.log") || fail "Cannot inspect provisioning profile distribution type."
    [[ "$forbidden_count" == 0 ]] || fail "Provisioning profile is development, ad-hoc, or enterprise, not App Store distribution."
    team=$(plist_value "$profile" TeamIdentifier.0 string) || fail "Provisioning profile has no team identifier."
    [[ "$team" =~ ^[A-Z0-9]{10}$ ]] || fail "Provisioning profile team identifier is malformed."
    app_id_prefix=$(plist_value "$profile" ApplicationIdentifierPrefix.0 string) ||
        fail "Provisioning profile has no application identifier prefix."
    [[ "$app_id_prefix" =~ ^[A-Z0-9]{10}$ ]] || fail "Provisioning profile application identifier prefix is malformed."
    profile_id=$(plist_value "$profile" Entitlements.application-identifier string) ||
        fail "Provisioning profile has no application-identifier entitlement."
    expect_equal "Provisioning profile application identifier" "$profile_id" "$app_id_prefix.$app_id"
    get_task_allow=$(plist_value "$profile" Entitlements.get-task-allow bool) ||
        fail "Provisioning profile must have a boolean get-task-allow entitlement."
    [[ "$get_task_allow" == false ]] || fail "Provisioning profile permits debugging; App Store distribution is required."
    expiration=$(plist_value "$profile" ExpirationDate date) || fail "Provisioning profile has no valid ExpirationDate."
    [[ "$expiration" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] ||
        fail "Provisioning profile expiration date is malformed."
    now=$(date -u '+%Y-%m-%dT%H:%M:%SZ') || fail "Cannot read current time."
    [[ "$now" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] ||
        fail "Current UTC time is malformed."
    [[ "$expiration" > "$now" ]] || fail "Provisioning profile has expired."
}

case "$platform" in
    android) verify_android ;;
    ios) verify_ios ;;
esac
printf 'Verified %s package: %s (version %s, build %s).\n' "$platform" "$app_id" "$version" "$build"
