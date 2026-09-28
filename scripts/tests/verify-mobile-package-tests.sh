#!/bin/bash
# No mobile SDK, signing assets, network, or third-party test framework required.
set -euo pipefail
test_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repository=$(cd -- "$test_directory/../.." && pwd -P)
cd "$repository"
fixture_relative="scripts/tests/.verify-mobile-package-tests.$$"
mkdir "$fixture_relative"
fixture="$repository/$fixture_relative"
trap 'rm -rf -- "$fixture"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir -p "$fixture/checkout/scripts/lib" "$fixture/bin" "$fixture/cases"
cp scripts/lib/verify-mobile-package.sh "$fixture/checkout/scripts/lib/"
helper="$fixture/checkout/scripts/lib/verify-mobile-package.sh"
printf 'mock archive\n' > "$fixture/package with spaces.zip"
printf 'mock bundletool\n' > "$fixture/bundletool with spaces.jar"
export BUNDLETOOL_JAR="$fixture/bundletool with spaces.jar"
export MOCK_APP_ID=com.golf1052.SeattleCarsInBikeLanes.Mobile
export MOCK_VERSION=1.0.1 MOCK_BUILD=4
export MOCK_SHA1=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
export MOCK_SHA256=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB

cat > "$fixture/bin/mock-tool" <<'MOCK'
#!/bin/bash
set -euo pipefail
[[ "${LC_ALL:-}" == C && "${LANG:-}" == C ]] || exit 98
tool=${0##*/}
scenario=${MOCK_CASE:-success}
case "$tool" in
    zipinfo)
        [[ "$scenario" != zip_list_failure ]] || exit 1
        if [[ "$1" == -1 ]]; then
            case "$scenario" in
                absolute_zip) printf '/outside\n' ;;
                parent_zip) printf 'Payload/../../outside\n' ;;
                windows_zip) printf 'C:\\outside\n' ;;
                backslash_zip) printf 'Payload\\..\\outside\n' ;;
                escaped_control_zip) printf 'Payload/.^A./outside\n' ;;
                duplicate_zip) printf 'Payload/App.app/\nPayload/App.app\n' ;;
                empty_zip) : ;;
                *) printf 'Payload/App.app/Info.plist\nPayload/App.app/embedded.mobileprovision\n' ;;
            esac
        else
            [[ "$scenario" != zip_attributes_failure ]] || exit 1
            if [[ "$scenario" == symlink_zip ]]; then
                printf 'lrwxr-xr-x  3.0 unx 8 b- 8 stor Payload/link\n'
            else
                printf '%s\n' '-rw-r--r--  3.0 unx 8 b- 8 stor Payload/App.app/Info.plist'
            fi
        fi
        ;;
    unzip)
        [[ "$scenario" != corrupt_zip ]] || exit 2
        if [[ "$1" == -tqq ]]; then exit 0; fi
        [[ "$scenario" != unzip_failure ]] || exit 2
        destination=$4
        [[ "$scenario" != no_app ]] || exit 0
        mkdir -p "$destination/Payload/App.app"
        printf 'info\n' > "$destination/Payload/App.app/Info.plist"
        if [[ "$scenario" != missing_profile ]]; then
            printf 'profile\n' > "$destination/Payload/App.app/embedded.mobileprovision"
        fi
        if [[ "$scenario" == multiple_apps ]]; then mkdir -p "$destination/Payload/Other.app"; fi
        ;;
    java)
        [[ "$1" == -Duser.language=en && "$2" == -Duser.country=US && "$3" == -jar ]] || exit 97
        case "$5" in
            validate) [[ "$scenario" != bundle_validate_failure ]] ;;
            dump)
                [[ "$scenario" != manifest_dump_failure ]] || exit 1
                [[ "$6" == manifest && "$8" == --module=base ]] || exit 97
                printf '<manifest/>\n'
                ;;
            *) exit 97 ;;
        esac
        ;;
    xmllint)
        [[ "$scenario" != xml_failure ]] || exit 1
        if [[ "$*" == *--noout* ]]; then exit 0; fi
        expression=$3
        case "$expression" in
            *'/manifest/@package'*)
                if [[ "$scenario" == id_mismatch ]]; then printf 'wrong.id'; else printf '%s' "$MOCK_APP_ID"; fi ;;
            *'versionName'*)
                if [[ "$scenario" == version_mismatch ]]; then printf '9.9.9'; else printf '%s' "$MOCK_VERSION"; fi ;;
            *'versionCode'*)
                if [[ "$scenario" == build_mismatch ]]; then printf '99'; else printf '%s' "$MOCK_BUILD"; fi ;;
            'count(/plist/dict)')
                [[ "$scenario" != profile_xml_failure ]] || exit 1
                if [[ "$scenario" == profile_not_dictionary ]]; then printf 0; else printf 1; fi ;;
            *ProvisionedDevices*)
                case "$scenario" in
                    development_profile|adhoc_profile|enterprise_profile) printf 1 ;;
                    profile_distribution_command_failure) exit 1 ;;
                    *) printf 0 ;;
                esac ;;
            *) exit 97 ;;
        esac
        ;;
    jarsigner)
        [[ "$1" == -J-Duser.language=en && "$2" == -J-Duser.country=US && "$3" == -verify && "$4" == -strict ]] || exit 97
        case "$scenario" in
            unsigned) printf 'jar is unsigned.\n'; exit 0 ;;
            signature_failure) printf 'jarsigner: SecurityException: digest error\n'; exit 1 ;;
            no_signed_entries) printf 'jar verified.\n'; exit 0 ;;
        esac
        printf 'sm        10 Wed Sep 09 00:00:00 UTC 2026 base/manifest/AndroidManifest.xml\n\n'
        if [[ "$scenario" == self_signed ]]; then
            printf 'jar verified, with signer errors.\nThis jar contains entries whose signer certificate is self-signed.\n'
            exit 4
        fi
        printf 'jar verified.\n'
        case "$scenario" in
            unsigned_entries) printf 'This jar contains unsigned entries.\n'; exit 16 ;;
            unsigned_entries_status_zero) printf 'This jar contains unsigned entries.\n' ;;
            invalid_usage) exit 8 ;;
            expired_android_certificate) printf 'This jar contains entries whose signer certificate has expired.\n'; exit 4 ;;
            unknown_jarsigner_status) exit 64 ;;
        esac
        ;;
    keytool)
        [[ "$1" == -J-Duser.language=en && "$2" == -J-Duser.country=US && "$3" == -printcert && "$4" == -jarfile ]] || exit 97
        [[ "$scenario" != keytool_failure ]] || exit 1
        printf 'Signer #1:\n\nCertificate #1:\nCertificate fingerprints:\n\t SHA256: '
        case "$scenario" in
            wrong_signer) printf '%064d\n' 0 ;;
            malformed_fingerprint) printf 'not-a-fingerprint\n' ;;
            *) printf '%s\n' "$MOCK_SHA256" ;;
        esac
        if [[ "$scenario" == certificate_chain ]]; then
            printf 'Certificate #2:\nCertificate fingerprints:\n\t SHA256: %064d\n' 0
        fi
        if [[ "$scenario" == multiple_signers ]]; then
            printf 'Signer #2:\nCertificate #1:\n\t SHA256: %s\n' "$MOCK_SHA256"
        fi
        ;;
    plutil)
        case "$1" in
            -lint)
                [[ "$scenario" != plist_failure ]] || exit 1
                if [[ "$scenario" == malformed_profile && "$2" == */profile.plist ]]; then exit 1; fi ;;
            -convert)
                [[ "$scenario" != profile_conversion_failure ]] || exit 1
                printf '<plist><dict/></plist>\n' > "$4" ;;
            -extract)
                key=$2
                [[ "$scenario" != plist_extract_failure ]] || exit 1
                case "$key" in
                    CFBundleIdentifier)
                        if [[ "$scenario" == id_mismatch ]]; then printf 'wrong.id'; else printf '%s' "$MOCK_APP_ID"; fi ;;
                    CFBundleShortVersionString)
                        if [[ "$scenario" == version_mismatch ]]; then printf '9.9.9'; else printf '%s' "$MOCK_VERSION"; fi ;;
                    CFBundleVersion)
                        if [[ "$scenario" == build_mismatch ]]; then printf '99'; else printf '%s' "$MOCK_BUILD"; fi ;;
                    TeamIdentifier.0)
                        case "$scenario" in
                            missing_team) exit 1 ;;
                            malformed_team) printf invalid ;;
                            *) printf ABC1234567 ;;
                        esac ;;
                    ApplicationIdentifierPrefix.0)
                        case "$scenario" in
                            missing_prefix) exit 1 ;;
                            malformed_prefix) printf invalid ;;
                            distinct_prefix) printf ZZZ1234567 ;;
                            *) printf ABC1234567 ;;
                        esac ;;
                    Entitlements.application-identifier)
                        case "$scenario" in
                            profile_id_mismatch) printf 'ABC1234567.wrong.id' ;;
                            wildcard_profile) printf 'ABC1234567.*' ;;
                            wrong_profile_prefix|distinct_prefix) printf 'ZZZ1234567.%s' "$MOCK_APP_ID" ;;
                            missing_profile_id) exit 1 ;;
                            *) printf 'ABC1234567.%s' "$MOCK_APP_ID" ;;
                        esac ;;
                    Entitlements.get-task-allow)
                        [[ "$5" == bool ]] || exit 97
                        case "$scenario" in
                            missing_debug_entitlement|string_debug_entitlement) exit 1 ;;
                            debug_profile) printf true ;;
                            *) printf false ;;
                        esac ;;
                    ExpirationDate)
                        [[ "$5" == date ]] || exit 97
                        case "$scenario" in
                            missing_expiration) exit 1 ;;
                            malformed_expiration) printf invalid ;;
                            expired_profile) printf '2000-01-01T00:00:00Z' ;;
                            *) printf '2099-01-01T00:00:00Z' ;;
                        esac ;;
                    *) exit 97 ;;
                esac ;;
            *) exit 97 ;;
        esac
        ;;
    codesign)
        [[ "$scenario" != codesign_failure ]] || exit 1
        if [[ "$1" == --verify ]]; then
            [[ "$2" == --deep && "$3" == --strict ]] || exit 97
        else
            [[ "$1" == -d && "$2" == --extract-certificates=* && $# -eq 3 ]] || exit 97
            [[ "$scenario" != certificate_extraction_failure ]] || exit 1
            if [[ "$scenario" != adhoc_codesign ]]; then printf 'DER\n' > "${2#*=}0"; fi
        fi
        ;;
    openssl)
        [[ "$scenario" != openssl_failure ]] || exit 1
        if [[ "$scenario" == wrong_signer ]]; then printf 'SHA1 Fingerprint=%040d\n' 0
        elif [[ "$scenario" == malformed_fingerprint ]]; then printf 'SHA1 Fingerprint=invalid\n'
        else printf 'sha1 Fingerprint=%s\n' "$MOCK_SHA1"; fi
        ;;
    security)
        [[ "$scenario" != cms_failure ]] || exit 1
        [[ "$1" == cms && "$2" == -D && "$5" == -o ]] || exit 97
        printf 'profile\n' > "$6"
        ;;
    date)
        [[ "$scenario" != date_failure ]] || exit 1
        if [[ "$scenario" == malformed_clock ]]; then printf invalid
        else printf '2026-09-09T00:00:00Z\n'; fi
        ;;
    *) exit 97 ;;
esac
MOCK
chmod +x "$fixture/bin/mock-tool"
for tool in zipinfo unzip java xmllint jarsigner keytool plutil codesign openssl security date; do
    ln -s mock-tool "$fixture/bin/$tool"
done
export PATH="$fixture/bin:$PATH"
passed=0
failed=0

run_case() {
    local platform=$1 scenario=$2 expected=$3 diagnostic=${4:-}
    local directory package signer status scratch
    directory="$fixture/cases/$platform-$scenario"
    mkdir -p "$directory/scratch"
    scratch="$directory/scratch"
    package="$fixture/package with spaces.zip"
    if [[ "$platform" == ios ]]; then signer=$MOCK_SHA1; else signer=$MOCK_SHA256; fi
    case "$scenario" in
        absent_package) package="$fixture/not-present.zip" ;;
        empty_package) package="$directory/empty.zip"; : > "$package" ;;
        dirty_scratch) printf 'retain me\n' > "$directory/scratch/sentinel" ;;
        hidden_scratch) printf 'retain me\n' > "$directory/scratch/.sentinel" ;;
        missing_scratch) scratch="$directory/not-present" ;;
        symlink_scratch) ln -s scratch "$directory/link"; scratch="$directory/link" ;;
        checkout_scratch) scratch="$fixture/checkout/verification-$platform"; mkdir "$scratch" ;;
        invalid_expected_fingerprint) signer=invalid ;;
        normalized_expected_fingerprint)
            if [[ "$platform" == ios ]]; then
                signer=aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa:aa
            else
                signer=bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb:bb
            fi ;;
    esac
    if MOCK_CASE="$scenario" bash "$helper" "$platform" "$package" "$MOCK_APP_ID" "$MOCK_VERSION" \
        "$MOCK_BUILD" "$scratch" "$signer" > "$directory/output.log" 2>&1; then
        status=0
    else
        status=$?
    fi
    if { [[ "$expected" == pass && "$status" == 0 ]] ||
         [[ "$expected" == fail && "$status" != 0 ]]; } &&
       { [[ -z "$diagnostic" ]] || grep -Fq "$diagnostic" "$directory/output.log"; }; then
        if [[ "$expected" == fail ]] && grep -Fq 'Verified ' "$directory/output.log"; then
            printf 'FAIL %s/%s announced success before failing\n' "$platform" "$scenario"
            failed=$((failed + 1))
            return
        fi
        passed=$((passed + 1))
    else
        printf 'FAIL %s/%s (expected %s; exit %s)\n' "$platform" "$scenario" "$expected" "$status"
        cat "$directory/output.log"
        failed=$((failed + 1))
    fi
    if [[ "$scenario" == dirty_scratch && ! -f "$directory/scratch/sentinel" ]] ||
       [[ "$scenario" == hidden_scratch && ! -f "$directory/scratch/.sentinel" ]]; then
        printf 'FAIL verifier removed pre-existing scratch contents\n'
        failed=$((failed + 1))
    fi
}

for platform in ios android; do
    run_case "$platform" success pass 'Verified '
    run_case "$platform" normalized_expected_fingerprint pass 'Verified '
    run_case "$platform" absent_package fail 'Package is missing'
    run_case "$platform" empty_package fail 'Package is missing'
    run_case "$platform" dirty_scratch fail 'Scratch directory must be empty'
    run_case "$platform" hidden_scratch fail 'Scratch directory must be empty'
    run_case "$platform" missing_scratch fail 'Scratch must be an existing'
    run_case "$platform" symlink_scratch fail 'not a symlink'
    run_case "$platform" checkout_scratch fail 'outside the checkout'
    run_case "$platform" invalid_expected_fingerprint fail 'certificate fingerprint'
    for scenario in absolute_zip parent_zip windows_zip backslash_zip escaped_control_zip; do
        run_case "$platform" "$scenario" fail 'unsafe path'
    done
    run_case "$platform" duplicate_zip fail 'duplicate paths'
    run_case "$platform" symlink_zip fail 'symlink or special file'
    run_case "$platform" empty_zip fail 'ZIP is empty'
    run_case "$platform" zip_list_failure fail 'Cannot list package'
    run_case "$platform" zip_attributes_failure fail 'Cannot inspect ZIP'
    run_case "$platform" corrupt_zip fail 'ZIP integrity check failed'
    for scenario in id_mismatch version_mismatch build_mismatch wrong_signer; do
        run_case "$platform" "$scenario" fail 'does not match'
    done
    run_case "$platform" malformed_fingerprint fail 'malformed'
done

run_case windows unknown_platform fail 'Platform must be ios or android'
run_case android self_signed pass 'Verified '
run_case android certificate_chain pass 'Verified '
run_case android bundle_validate_failure fail 'bundletool validate failed'
run_case android manifest_dump_failure fail 'manifest inspection failed'
run_case android xml_failure fail 'valid XML manifest'
run_case android unsigned fail 'unsigned'
run_case android no_signed_entries fail 'did not confirm any signed entries'
run_case android signature_failure fail 'JAR signature verification failed'
run_case android unsigned_entries fail 'status 16'
run_case android unsigned_entries_status_zero fail 'unsigned entries'
run_case android invalid_usage fail 'status 8'
run_case android expired_android_certificate fail 'invalid signing certificate'
run_case android unknown_jarsigner_status fail 'status 64'
run_case android keytool_failure fail 'Cannot inspect the Android signing certificate'
run_case android multiple_signers fail 'exactly one identifiable signing leaf'
saved_bundletool=$BUNDLETOOL_JAR
export BUNDLETOOL_JAR="$fixture/missing-bundletool.jar"
run_case android missing_bundletool fail 'Set BUNDLETOOL_JAR'
export BUNDLETOOL_JAR=$saved_bundletool

run_case ios unzip_failure fail 'IPA extraction failed'
run_case ios no_app fail 'exactly one Payload/*.app'
run_case ios multiple_apps fail 'exactly one Payload/*.app'
run_case ios plist_failure fail 'Info.plist is malformed'
run_case ios plist_extract_failure fail 'Cannot read CFBundleIdentifier'
run_case ios codesign_failure fail 'code signature verification failed'
run_case ios certificate_extraction_failure fail 'Cannot extract the iOS signing certificate'
run_case ios adhoc_codesign fail 'no signing leaf certificate'
run_case ios openssl_failure fail 'Cannot read iOS signing certificate'
run_case ios missing_profile fail 'missing its embedded App Store'
run_case ios cms_failure fail 'Cannot decode the embedded'
run_case ios malformed_profile fail 'provisioning profile is malformed'
run_case ios profile_conversion_failure fail 'Cannot convert'
run_case ios profile_xml_failure fail 'Cannot inspect provisioning profile structure'
run_case ios profile_not_dictionary fail 'must be a plist dictionary'
for scenario in development_profile adhoc_profile enterprise_profile; do
    run_case ios "$scenario" fail 'not App Store distribution'
done
run_case ios profile_distribution_command_failure fail 'Cannot inspect provisioning profile distribution type'
run_case ios missing_team fail 'no team identifier'
run_case ios malformed_team fail 'team identifier is malformed'
run_case ios distinct_prefix pass 'Verified '
run_case ios missing_prefix fail 'no application identifier prefix'
run_case ios malformed_prefix fail 'application identifier prefix is malformed'
for scenario in profile_id_mismatch wildcard_profile wrong_profile_prefix; do
    run_case ios "$scenario" fail 'profile application identifier does not match'
done
run_case ios missing_profile_id fail 'no application-identifier entitlement'
run_case ios missing_debug_entitlement fail 'boolean get-task-allow'
run_case ios string_debug_entitlement fail 'boolean get-task-allow'
run_case ios debug_profile fail 'permits debugging'
run_case ios missing_expiration fail 'no valid ExpirationDate'
run_case ios malformed_expiration fail 'expiration date is malformed'
run_case ios expired_profile fail 'has expired'
run_case ios date_failure fail 'Cannot read current time'
run_case ios malformed_clock fail 'Current UTC time is malformed'

printf 'Package verifier tests: %s passed, %s failed.\n' "$passed" "$failed"
[[ "$failed" == 0 ]]
