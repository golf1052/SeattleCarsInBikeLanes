#!/bin/bash

set -euo pipefail

test_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_script="$test_dir/../release-mobile.sh"
suite_dir="$test_dir/.release-mobile-tests-$$"
original_path="$PATH"
passed=0
failed=0

[[ "$(uname -s)" == Darwin ]] || { echo 'These tests require macOS (including PlistBuddy).' >&2; exit 1; }
for tool in git jq xmllint shasum; do
    command -v "$tool" >/dev/null || { echo "Missing test prerequisite: $tool" >&2; exit 1; }
done
mkdir "$suite_dir"
trap 'rm -rf "$suite_dir"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
cp "$source_script" "$suite_dir/release-mobile.sh"
mkdir "$suite_dir/mocks"

cat > "$suite_dir/mocks/dotnet" <<'MOCK'
#!/bin/bash
set -euo pipefail
printf 'dotnet:%s\n' "$1" >> "$FIXTURE/events"
if [[ "$1" == --version ]]; then
    if [[ -n "${MOCK_EARLY_VERSION_FILE:-}" ]]; then
        cp "$MOCK_EARLY_VERSION_FILE" "$FIXTURE_REPO/SeattleCarsInBikeLanes.Mobile/Version.props"
    fi
    printf '%s\n' "${MOCK_DOTNET_VERSION:-10.0.100}"
    exit 0
fi
command="$1"
shift
target=
publish_dir=
artifacts_dir=
version=
build=
previous=
for arg in "$@"; do
    case "$arg" in
        -p:TargetFramework=net10.0-*) target="${arg##*-}" ;;
        -p:MobileReleasePlatform=*) target="${arg#*=}" ;;
        net10.0-ios) target=ios ;;
        net10.0-android) target=android ;;
        -p:PublishDir=*) publish_dir="${arg#*=}" ;;
        -p:ApplicationDisplayVersion=*) version="${arg#*=}" ;;
        -p:ApplicationVersion=*) build="${arg#*=}" ;;
    esac
    if [[ "$previous" == --artifacts-path ]]; then artifacts_dir="$arg"; fi
    previous="$arg"
done
[[ "$target" == ios || "$target" == android ]]
printf 'dotnet:%s:%s\n' "$command" "$target" >> "$FIXTURE/events"
printf '%s\n' "$@" > "$FIXTURE/$command-$target.args"
case "$command" in
    restore)
        [[ "${MOCK_FAIL_RESTORE:-}" != "$target" ]] || exit 40
        mkdir -p "$artifacts_dir"
        ;;
    msbuild)
        [[ "${MOCK_FAIL_QUERY:-}" != "$target" ]] || exit 41
        if [[ "${MOCK_FAIL_TOOLING:-}" == "$target" && " $* " == *' -t:GetAndroidDependencies '* ]]; then exit 51; fi
        cat "$FIXTURE/properties-$target.json"
        ;;
    publish)
        [[ -n "$publish_dir" && -n "$artifacts_dir" && -n "$version" && -n "$build" ]]
        if [[ "${MOCK_PAUSE_PLATFORM:-}" == "$target" ]]; then
            touch "$FIXTURE/publish-entered"
            for ((attempt = 0; attempt < 200; attempt++)); do
                [[ ! -e "$FIXTURE/publish-continue" ]] || break
                sleep 0.1
            done
            [[ -e "$FIXTURE/publish-continue" ]] || exit 42
        fi
        [[ "${MOCK_FAIL_PUBLISH:-}" != "$target" ]] || exit 43
        mkdir -p "$publish_dir" "$artifacts_dir"
        if [[ "$target" == ios ]]; then
            artifact='Fixture App.ipa'
            mkdir -p "$artifacts_dir/Fixture App.app.dSYM/Contents/Resources/DWARF"
            printf 'fixture symbols\n' > "$artifacts_dir/Fixture App.app.dSYM/Contents/Resources/DWARF/Fixture App"
        else
            artifact='Fixture App-signed.aab'
            printf 'unsigned intermediate\n' > "$publish_dir/Fixture App.aab"
        fi
        case "${MOCK_ARTIFACT_MODE:-}:$target" in
            missing:ios|missing:android) ;;
            empty:ios|empty:android) : > "$publish_dir/$artifact" ;;
            multiple:ios|multiple:android)
                printf 'package %s %s %s\n' "$target" "$version" "$build" > "$publish_dir/$artifact"
                cp "$publish_dir/$artifact" "$publish_dir/duplicate-$artifact"
                ;;
            *) printf 'package %s %s %s\n' "$target" "$version" "$build" > "$publish_dir/$artifact" ;;
        esac
        if [[ "${MOCK_EDIT_VERSION:-}" == "$target" ]]; then
            printf '<!-- external edit -->\n' >> "$FIXTURE_REPO/SeattleCarsInBikeLanes.Mobile/Version.props"
        fi
        if [[ "${MOCK_OUTPUT_COLLISION:-}" == "$target" ]]; then
            mkdir -p "$MOCK_OUTPUT/$version-$build"
            printf 'do not overwrite\n' > "$MOCK_OUTPUT/$version-$build/external"
        fi
        ;;
    *) exit 44 ;;
esac
MOCK

cat > "$suite_dir/mocks/platform-tool" <<'MOCK'
#!/bin/bash
set -euo pipefail
tool="${0##*/}"
printf '%s:%s\n' "$tool" "$*" >> "$FIXTURE/events"
[[ "${MOCK_FAIL_TOOL:-}" != "$tool" && "${MOCK_FAIL_TOOL:-}" != all ]] || exit 45
case "$tool" in
    security)
        case "$1" in
            find-identity) printf '  1) AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA "Apple Distribution: Fixture (FIXTURE123)"\n' ;;
            cms)
                [[ "$2" == -D && "$3" == -i ]]
                cat "$4"
                ;;
            *) exit 46 ;;
        esac
        ;;
    keytool)
        previous=
        for arg in "$@"; do
            if [[ "$previous" == -file ]]; then printf 'fixture upload certificate\n' > "$arg"; fi
            previous="$arg"
        done
        ;;
    java) printf '1.18.0\n' ;;
    xcrun) printf '%s\n' "$FIXTURE/iphone sdk" ;;
    xcode-select) printf '%s\n' "$FIXTURE/xcode" ;;
    codesign|plutil|jarsigner) ;;
    *) exit 47 ;;
esac
MOCK

cat > "$suite_dir/mocks/mv" <<'MOCK'
#!/bin/bash
set -euo pipefail
if [[ "${MOCK_FAIL_FINALIZE:-}" == version && "$2" == "$FIXTURE_REPO/SeattleCarsInBikeLanes.Mobile/Version.props" ]]; then
    echo 'fixture: version rename failed' >&2
    exit 48
fi
if [[ "${MOCK_FAIL_FINALIZE:-}" == staging && "$1" == "$MOCK_OUTPUT"/.staging-* ]]; then
    echo 'fixture: staging rename failed' >&2
    exit 49
fi
exec /bin/mv "$@"
MOCK

cat > "$suite_dir/mocks/verify-mobile-package.sh" <<'MOCK'
#!/bin/bash
set -euo pipefail
[[ $# -eq 7 && -s "$2" && -d "$6" ]]
printf 'verify:%s\n' "$1" >> "$FIXTURE/events"
printf '%s\n' "$@" > "$FIXTURE/verify-$1.args"
[[ "${MOCK_FAIL_VERIFY:-}" != "$1" ]] || exit 50
[[ "$3" == "com.fixture.$1" && -n "$4" && -n "$5" && -n "$7" ]]
MOCK
chmod +x "$suite_dir/mocks/"*

fail_test() { printf '  %s\n' "$*" >&2; exit 1; }
assert_file() { [[ -f "$1" ]] || fail_test "Missing file: $1"; }
assert_absent() { [[ ! -e "$1" ]] || fail_test "Unexpected path: $1"; }
assert_contains() { grep -Fq -- "$2" "$1" || fail_test "Missing '$2' in $1"; }
assert_arg() { grep -Fxq -- "$2" "$1" || fail_test "Missing argument '$2' in $1"; }
assert_not_contains() { ! grep -Fq -- "$2" "$1" || fail_test "Unexpected '$2' in $1"; }

new_fixture() {
    fixture_number=$((fixture_number + 1))
    export FIXTURE="$case_dir/fixture $fixture_number"
    export FIXTURE_REPO="$FIXTURE/repository with spaces"
    export HOME="$FIXTURE/home with spaces"
    export TMPDIR="$HOME/scratch"
    export MOCK_OUTPUT="$HOME/Library/Developer/SeattleCarsInBikeLanes/Releases"
    export BUNDLETOOL_JAR="$FIXTURE/bundletool.jar"
    export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1
    export PATH="$FIXTURE/bin:/usr/bin:/bin:/usr/sbin:/sbin:$original_path"
    unset MOCK_DOTNET_VERSION MOCK_FAIL_QUERY MOCK_PAUSE_PLATFORM MOCK_FAIL_PUBLISH
    unset MOCK_ARTIFACT_MODE MOCK_EDIT_VERSION MOCK_OUTPUT_COLLISION MOCK_FAIL_TOOL
    unset MOCK_FAIL_FINALIZE MOCK_FAIL_VERIFY
    unset MOCK_FAIL_RESTORE MOCK_FAIL_TOOLING
    unset MOCK_EARLY_VERSION_FILE
    expected_cache_count=0
    mkdir -p "$FIXTURE_REPO/scripts/lib" "$FIXTURE_REPO/SeattleCarsInBikeLanes.Mobile" \
        "$FIXTURE/bin" "$FIXTURE/android sdk/platforms" "$FIXTURE/android sdk/build-tools" \
        "$FIXTURE/java sdk/bin" "$HOME/Library/MobileDevice/Provisioning Profiles" "$TMPDIR"
    cp "$suite_dir/release-mobile.sh" "$FIXTURE_REPO/scripts/release-mobile.sh"
    cp "$suite_dir/mocks/verify-mobile-package.sh" "$FIXTURE_REPO/scripts/lib/"
    cp "$suite_dir/mocks/dotnet" "$suite_dir/mocks/mv" "$FIXTURE/bin/"
    local tool
    for tool in security codesign xcrun xcode-select plutil; do
        cp "$suite_dir/mocks/platform-tool" "$FIXTURE/bin/$tool"
    done
    for tool in java keytool jarsigner; do
        cp "$suite_dir/mocks/platform-tool" "$FIXTURE/java sdk/bin/$tool"
    done
    version_file="$FIXTURE_REPO/SeattleCarsInBikeLanes.Mobile/Version.props"
    seed_version 1.0.0 3
    printf '<Project />\n' > "$FIXTURE_REPO/SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.csproj"
    printf '<Project />\n' > "$FIXTURE/jpeg dependency.csproj"
    printf 'fixture keystore\n' > "$FIXTURE/upload.keystore"
    printf 'fixture-password\n' > "$FIXTURE/key password"
    printf 'fixture-password\n' > "$FIXTURE/store password"
    printf 'fixture jar\n' > "$BUNDLETOOL_JAR"
    : > "$FIXTURE/events"
    cat > "$HOME/Library/MobileDevice/Provisioning Profiles/fixture.mobileprovision" <<'PROFILE'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Name</key><string>Fixture App Store</string>
<key>UUID</key><string>11111111-2222-3333-4444-555555555555</string>
</dict></plist>
PROFILE
    jq -n --arg dependency "$FIXTURE/jpeg dependency.csproj" \
        '{Properties:{ApplicationId:"com.fixture.ios",JpegXmpWritePluginMDEProject:$dependency,
          CodesignKey:"Apple Distribution: Fixture (FIXTURE123)",CodesignProvision:"Fixture App Store"}}' \
        > "$FIXTURE/properties-ios.json"
    jq -n --arg dependency "$FIXTURE/jpeg dependency.csproj" --arg root "$FIXTURE" \
        '{Properties:{ApplicationId:"com.fixture.android",JpegXmpWritePluginMDEProject:$dependency,
          GOOGLE_MAPS_API_KEY:"fixture-maps-key",AndroidSigningKeyStore:($root+"/upload.keystore"),
          AndroidSigningKeyAlias:"fixture",AndroidSigningKeyPass:("file:"+$root+"/key password"),
          AndroidSigningStorePass:("file:"+$root+"/store password"),
          AndroidSdkDirectory:($root+"/android sdk"),JavaSdkDirectory:($root+"/java sdk"),
          MonoAndroidToolsDirectory:$root,AndroidBundleToolJarPath:($root+"/bundletool.jar")}}' \
        > "$FIXTURE/properties-android.json"
    printf '.mobile-release.lock/\n' > "$FIXTURE_REPO/.gitignore"
    git -c init.defaultBranch=main init -q "$FIXTURE_REPO"
    git -C "$FIXTURE_REPO" config user.name 'Release fixture'
    git -C "$FIXTURE_REPO" config user.email 'release-fixture@example.invalid'
    git -C "$FIXTURE_REPO" config commit.gpgsign false
    git -C "$FIXTURE_REPO" config core.hooksPath /dev/null
    git -C "$FIXTURE_REPO" add .
    git -C "$FIXTURE_REPO" commit -qm 'Fixture baseline'
    initial_revision="$(git -C "$FIXTURE_REPO" rev-parse HEAD)"
    capture="$case_dir/output.log"
}

seed_version() {
    printf '<Project>\n  <PropertyGroup>\n    <ApplicationDisplayVersion>%s</ApplicationDisplayVersion>\n    <ApplicationVersion>%s</ApplicationVersion>\n  </PropertyGroup>\n</Project>\n' \
        "$1" "$2" > "$version_file"
    cp "$version_file" "$case_dir/baseline.props"
}

invoke() {
    (cd "$case_dir" && /bin/bash "$FIXTURE_REPO/scripts/release-mobile.sh" "$@") > "$capture" 2>&1
}

expect_failure() {
    local expected="$1"
    shift
    if invoke "$@"; then fail_test "Command unexpectedly succeeded: $*"; fi
    assert_contains "$capture" "$expected"
}

assert_clean() {
    assert_absent "$FIXTURE_REPO/.mobile-release.lock"
    local leftover
    for leftover in "$MOCK_OUTPUT"/.staging-* "$version_file".tmp.*; do
        assert_absent "$leftover"
    done
}

assert_baseline() {
    cmp -s "$version_file" "$case_dir/baseline.props" || fail_test 'Stored baseline changed on failure'
    assert_clean
    [[ "$(git -C "$FIXTURE_REPO" rev-parse HEAD)" == "$initial_revision" ]] || fail_test 'Release created a commit'
}

assert_no_release() {
    local release
    for release in "$MOCK_OUTPUT"/*; do assert_absent "$release"; done
}

assert_release() {
    local version="$1" build="$2" selected="$3" release target path digest cache_count=0
    release="$MOCK_OUTPUT/$version-$build"
    assert_file "$release/COMPLETE"
    assert_file "$release/release.json"
    [[ "$(xmllint --nonet --xpath 'string(/Project/PropertyGroup/ApplicationDisplayVersion)' "$version_file")" == "$version" ]] || fail_test 'Incorrect stored version'
    [[ "$(xmllint --nonet --xpath 'string(/Project/PropertyGroup/ApplicationVersion)' "$version_file")" == "$build" ]] || fail_test 'Incorrect stored build'
    jq -e --arg version "$version" --argjson build "$build" --arg platforms "$selected" \
        --arg revision "$initial_revision" \
        '.schema == 1 and .version == $version and .build == $build and
         .platforms == ($platforms | split(" ")) and .gitRevision == $revision and
         (.gitDirty | type == "boolean") and (.artifacts | length > 0)' \
        "$release/release.json" >/dev/null || fail_test 'Incorrect release manifest'
    while IFS=$'\t' read -r path digest; do
        assert_file "$release/$path"
        [[ "$(shasum -a 256 "$release/$path" | cut -d ' ' -f 1)" == "$digest" ]] || fail_test "Incorrect checksum: $path"
    done < <(jq -r '.artifacts[] | [.path, .sha256] | @tsv' "$release/release.json")
    for target in $selected; do
        assert_arg "$FIXTURE/publish-$target.args" "-p:ApplicationDisplayVersion=$version"
        assert_arg "$FIXTURE/publish-$target.args" "-p:ApplicationVersion=$build"
        assert_arg "$FIXTURE/publish-$target.args" "-p:MobileReleasePlatform=$target"
        assert_arg "$FIXTURE/publish-$target.args" '-p:MobileReleasePackaging=true'
        assert_arg "$FIXTURE/publish-$target.args" 'Release'
        assert_arg "$FIXTURE/publish-$target.args" "net10.0-$target"
        assert_arg "$FIXTURE/publish-$target.args" '--artifacts-path'
        assert_arg "$FIXTURE/publish-$target.args" "$FIXTURE_REPO/SeattleCarsInBikeLanes.Mobile/SeattleCarsInBikeLanes.Mobile.csproj"
        assert_arg "$FIXTURE/msbuild-$target.args" '-p:MobileReleasePackaging=true'
        assert_arg "$FIXTURE/msbuild-$target.args" "-p:MobileReleasePlatform=$target"
        assert_arg "$FIXTURE/verify-$target.args" "$version"
        assert_arg "$FIXTURE/verify-$target.args" "$build"
        assert_arg "$FIXTURE/verify-$target.args" "com.fixture.$target"
        case "$target" in
            ios)
                assert_file "$release/ios/Fixture App.ipa"
                assert_file "$release/ios/Fixture App.app.dSYM/Contents/Resources/DWARF/Fixture App"
                assert_arg "$FIXTURE/publish-ios.args" 'ios-arm64'
                assert_arg "$FIXTURE/publish-ios.args" '-p:BuildIpa=true'
                assert_arg "$FIXTURE/publish-ios.args" '-p:ArchiveOnBuild=true'
                assert_arg "$FIXTURE/verify-ios.args" AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
                ;;
            android)
                assert_file "$release/android/Fixture App-signed.aab"
                assert_absent "$release/android/Fixture App.aab"
                assert_arg "$FIXTURE/publish-android.args" '-p:AndroidKeyStore=true'
                assert_arg "$FIXTURE/publish-android.args" '-p:AndroidPackageFormats=aab'
                digest="$(printf 'fixture upload certificate\n' | shasum -a 256)"
                assert_arg "$FIXTURE/verify-android.args" "${digest%% *}"
                ;;
        esac
    done
    assert_not_contains "$release/release.json" 'fixture-password'
    assert_not_contains "$release/release.json" 'fixture-maps-key'
    assert_clean
    [[ "$(git -C "$FIXTURE_REPO" rev-parse HEAD)" == "$initial_revision" ]] || fail_test 'Release created a commit'
    [[ -z "$(git -C "$FIXTURE_REPO" tag)" ]] || fail_test 'Release created a tag'
    for path in "$HOME/Library/Caches/SeattleCarsInBikeLanes"/release.*; do
        if [[ -d "$path" ]]; then cache_count=$((cache_count + 1)); fi
    done
    [[ "$cache_count" -eq "$expected_cache_count" ]] || fail_test 'Successful release left diagnostic files behind'
}

test_both() {
    new_fixture
    invoke
    assert_release 1.0.1 4 'ios android'
    jq -e '.previousVersion == "1.0.0" and .previousBuild == 3 and .gitDirty == false and (.artifacts | length == 3)' \
        "$MOCK_OUTPUT/1.0.1-4/release.json" >/dev/null
    assert_contains "$capture" 'No packages uploaded and no Git commit created.'
}

test_selections() {
    new_fixture
    rm -rf "$FIXTURE/android sdk" "$FIXTURE/java sdk" "$FIXTURE/upload.keystore"
    unset BUNDLETOOL_JAR
    invoke ios
    assert_release 1.0.1 4 ios
    assert_absent "$MOCK_OUTPUT/1.0.1-4/android"
    assert_not_contains "$FIXTURE/events" 'android'
    assert_not_contains "$FIXTURE/events" 'java:'
    assert_not_contains "$FIXTURE/events" 'keytool:'
    new_fixture
    rm -rf "$HOME/Library/MobileDevice" "$FIXTURE/properties-ios.json"
    export MOCK_FAIL_TOOL=security
    invoke android
    assert_release 1.0.1 4 android
    assert_absent "$MOCK_OUTPUT/1.0.1-4/ios"
    assert_not_contains "$FIXTURE/events" 'ios'
    assert_not_contains "$FIXTURE/events" 'security:'
    assert_not_contains "$FIXTURE/events" 'xcrun:'
}

test_bumps() {
    local mode expected
    for mode in patch minor major none; do
        new_fixture
        case "$mode" in patch) expected=1.0.1 ;; minor) expected=1.1.0 ;; major) expected=2.0.0 ;; none) expected=1.0.0 ;; esac
        invoke android --bump "$mode"
        assert_release "$expected" 4 android
    done
    new_fixture
    seed_version 2.7.9 3
    invoke android --bump minor
    assert_release 2.8.0 4 android
    new_fixture
    seed_version 2.7.9 3
    invoke android --bump major
    assert_release 3.0.0 4 android
}

test_overrides() {
    new_fixture
    invoke both --version 3.2.1 --build-number 25
    assert_release 3.2.1 25 'ios android'
    new_fixture
    invoke android --version 1.0.0
    assert_release 1.0.0 4 android
    new_fixture
    invoke ios --build-number 17
    assert_release 1.0.1 17 ios
    new_fixture
    invoke android --bump none --build-number 2100000000
    assert_release 1.0.0 2100000000 android
}

test_invalid_options() {
    new_fixture
    expect_failure 'Unknown argument' --wat
    expect_failure 'Specify only one platform' ios android
    expect_failure 'Specify only one platform' both both
    expect_failure 'Invalid --bump' --bump sideways
    expect_failure 'mutually exclusive' --version 1.2.3 --bump patch
    expect_failure 'Repeated --bump' --bump minor --bump major
    expect_failure 'Repeated --version' --version 1.2.3 --version 1.2.4
    expect_failure 'Repeated --build-number' --build-number 4 --build-number 5
    local option value
    for option in --bump --version --build-number --output-dir; do
        expect_failure 'requires a value' "$option"
        expect_failure 'requires a value' "$option" ''
        expect_failure 'requires a value' "$option" --dry-run
    done
    for value in 1 1.2 1.2.3.4 01.2.3 1.02.3 1.2.03 -1.2.3 1.a.3 '1.2.3 beta'; do
        expect_failure 'Invalid version' --version "$value"
    done
    expect_failure 'cannot go backwards' --version 0.9.9
    for value in 0 -1 04 abc 4.5 999999999999999999999; do
        expect_failure 'positive decimal integer' --build-number "$value"
    done
    expect_failure 'must exceed the stored baseline' --build-number 3
    expect_failure 'must exceed the stored baseline' --build-number 2
    expect_failure 'Google Play limit' --build-number 2100000001
    expect_failure 'Version component overflow' --version 1000000000.0.0
    assert_baseline
    assert_no_release
    [[ ! -s "$FIXTURE/events" ]] || fail_test 'Invalid arguments invoked toolchains'
}

test_overflow() {
    local version mode
    for mode in patch minor major; do
        new_fixture
        case "$mode" in patch) version=1.0.999999999 ;; minor) version=1.999999999.0 ;; major) version=999999999.0.0 ;; esac
        seed_version "$version" 3
        expect_failure 'Version component overflow' --bump "$mode"
        assert_baseline
        assert_no_release
    done
    new_fixture
    seed_version 1.0.0 2100000000
    expect_failure 'Google Play limit' --bump none
    assert_baseline
    new_fixture
    seed_version 1.0.0 03
    expect_failure 'positive decimal integer' --dry-run
    assert_baseline
}

snapshot() {
    find "$FIXTURE" -print | LC_ALL=C sort
    find "$FIXTURE" -type f -exec shasum -a 256 {} + | LC_ALL=C sort
}

test_dry_run_help() {
    new_fixture
    export MOCK_FAIL_TOOL=all MOCK_DOTNET_VERSION=0.0.0
    snapshot > "$case_dir/before"
    invoke both --dry-run
    assert_contains "$capture" 'Release 1.0.1, build 4'
    assert_contains "$capture" 'signed App Store IPA'
    assert_contains "$capture" 'signed Play AAB'
    snapshot > "$case_dir/after"
    cmp -s "$case_dir/before" "$case_dir/after" || fail_test 'Dry-run changed fixture files'
    assert_absent "$MOCK_OUTPUT"
    assert_absent "$HOME/Library/Caches"
    mkdir "$FIXTURE_REPO/.mobile-release.lock"
    printf 'existing owner\n' > "$FIXTURE_REPO/.mobile-release.lock/owner"
    snapshot > "$case_dir/before"
    invoke ios --dry-run --version 2.3.4 --build-number 20 --output-dir "$FIXTURE/new output"
    assert_contains "$capture" 'Release 2.3.4, build 20 (ios)'
    assert_not_contains "$capture" 'signed Play AAB'
    snapshot > "$case_dir/after"
    cmp -s "$case_dir/before" "$case_dir/after" || fail_test 'Dry-run touched an existing lock or output'
    rm "$version_file"
    snapshot > "$case_dir/before"
    invoke --help
    assert_contains "$capture" 'Usage: scripts/release-mobile.sh'
    invoke -h
    assert_contains "$capture" '--build-number'
    snapshot > "$case_dir/after"
    cmp -s "$case_dir/before" "$case_dir/after" || fail_test 'Help changed fixture files'
}

test_output_paths() {
    new_fixture
    export MOCK_OUTPUT="$FIXTURE/custom release output"
    invoke both --output-dir "$MOCK_OUTPUT"
    assert_release 1.0.1 4 'ios android'
    new_fixture
    export MOCK_OUTPUT="$case_dir/relative releases"
    invoke ios --output-dir 'relative releases'
    assert_release 1.0.1 4 ios
}

test_publish_failures_retry() {
    local target
    for target in ios android; do
        new_fixture
        export MOCK_FAIL_PUBLISH="$target"
        expect_failure "$target publish failed" both
        assert_baseline
        assert_no_release
        assert_contains "$capture" 'Private diagnostic files retained at:'
        if [[ "$target" == android ]]; then assert_contains "$FIXTURE/events" 'verify:ios'; fi
        unset MOCK_FAIL_PUBLISH
        find "$HOME/Library/Caches/SeattleCarsInBikeLanes" -type d -name 'release.*' | LC_ALL=C sort > "$case_dir/retained-before"
        expected_cache_count="$(wc -l < "$case_dir/retained-before" | tr -d ' ')"
        [[ "$expected_cache_count" -eq 1 ]] || fail_test 'Expected one diagnostic directory after publish failure'
        invoke both
        assert_release 1.0.1 4 'ios android'
        find "$HOME/Library/Caches/SeattleCarsInBikeLanes" -type d -name 'release.*' | LC_ALL=C sort > "$case_dir/retained-after"
        cmp -s "$case_dir/retained-before" "$case_dir/retained-after" || fail_test 'Retry altered previous diagnostics'
    done
}

test_artifact_failures() {
    local target mode
    for target in ios android; do
        for mode in missing empty multiple; do
            new_fixture
            export MOCK_ARTIFACT_MODE="$mode"
            expect_failure "Expected exactly one nonempty $target signed package" "$target"
            assert_baseline
            assert_no_release
            assert_not_contains "$FIXTURE/events" "verify:$target"
        done
    done
}

test_verifier_failures() {
    local target
    for target in ios android; do
        new_fixture
        export MOCK_FAIL_VERIFY="$target"
        expect_failure "$target package verification failed" both
        assert_baseline
        assert_no_release
        if [[ "$target" == android ]]; then assert_contains "$FIXTURE/events" 'verify:ios'; fi
    done
}

test_preflight_failures() {
    local target tool
    for target in ios android; do
        new_fixture
        export MOCK_FAIL_QUERY="$target"
        expect_failure "Cannot evaluate $target release configuration" both
        assert_baseline
        assert_no_release
        assert_not_contains "$FIXTURE/events" 'dotnet:publish'
    done
    for tool in xcrun security java keytool; do
        new_fixture
        export MOCK_FAIL_TOOL="$tool"
        if invoke both; then fail_test "Preflight unexpectedly accepted failing $tool"; fi
        assert_baseline
        assert_no_release
        assert_not_contains "$FIXTURE/events" 'dotnet:publish'
    done
    new_fixture
    export MOCK_DOTNET_VERSION=9.0.100
    expect_failure 'Select a .NET 10 SDK' android
    assert_baseline
    new_fixture
    export BUNDLETOOL_JAR="$FIXTURE/missing-bundletool.jar"
    expect_failure 'Cannot find the workload bundletool' android
    assert_baseline
    new_fixture
    export MOCK_FAIL_RESTORE=android
    expect_failure 'Android restore failed' android
    assert_baseline
    assert_not_contains "$FIXTURE/events" 'dotnet:publish'
    new_fixture
    export MOCK_FAIL_TOOLING=android
    expect_failure 'Cannot resolve the Android SDK/JDK' android
    assert_baseline
    assert_not_contains "$FIXTURE/events" 'dotnet:publish'
}

test_bundletool_discovery() {
    new_fixture
    unset BUNDLETOOL_JAR
    invoke android
    assert_release 1.0.1 4 android
    assert_contains "$FIXTURE/events" "java:-jar $FIXTURE/bundletool.jar version"
    new_fixture
    unset BUNDLETOOL_JAR
    jq 'del(.Properties.AndroidBundleToolJarPath)' "$FIXTURE/properties-android.json" > "$case_dir/properties.json"
    cp "$case_dir/properties.json" "$FIXTURE/properties-android.json"
    invoke android
    assert_release 1.0.1 4 android
    assert_contains "$FIXTURE/events" "java:-jar $FIXTURE/bundletool.jar version"
}

test_existing_lock() {
    new_fixture
    mkdir "$FIXTURE_REPO/.mobile-release.lock"
    printf 'another process\n' > "$FIXTURE_REPO/.mobile-release.lock/owner"
    expect_failure 'Release lock exists' both
    assert_contains "$FIXTURE_REPO/.mobile-release.lock/owner" 'another process'
    cmp -s "$version_file" "$case_dir/baseline.props"
    [[ ! -s "$FIXTURE/events" ]] || fail_test 'Locked invocation ran toolchains'
    rm -rf "$FIXTURE_REPO/.mobile-release.lock"
    assert_baseline
}

test_concurrent_lock() {
    new_fixture
    export MOCK_PAUSE_PLATFORM=ios
    /bin/bash "$FIXTURE_REPO/scripts/release-mobile.sh" ios > "$case_dir/first.log" 2>&1 &
    local release_pid=$! attempt
    trap 'kill "$release_pid" 2>/dev/null || :; wait "$release_pid" 2>/dev/null || :' EXIT
    for ((attempt = 0; attempt < 200; attempt++)); do
        [[ ! -e "$FIXTURE/publish-entered" ]] || break
        kill -0 "$release_pid" 2>/dev/null || fail_test 'First release exited before acquiring its lock'
        sleep 0.1
    done
    assert_file "$FIXTURE/publish-entered"
    expect_failure 'Release lock exists' android
    assert_file "$FIXTURE_REPO/.mobile-release.lock/owner"
    cmp -s "$version_file" "$case_dir/baseline.props"
    touch "$FIXTURE/publish-continue"
    wait "$release_pid"
    trap - EXIT
    assert_release 1.0.1 4 ios
    assert_not_contains "$FIXTURE/events" 'dotnet:publish:android'
}

test_source_edit() {
    new_fixture
    export MOCK_EDIT_VERSION=android
    expect_failure 'Version.props changed during the build' both
    cp "$case_dir/baseline.props" "$case_dir/expected.props"
    printf '<!-- external edit -->\n' >> "$case_dir/expected.props"
    cmp -s "$version_file" "$case_dir/expected.props" || fail_test 'Concurrent source edit was overwritten'
    assert_clean
    assert_no_release
}

test_early_source_edit() {
    new_fixture
    export MOCK_EARLY_VERSION_FILE="$FIXTURE/early-edit.props"
    sed -e 's/>1.0.0</>1.2.0</' -e 's/>3</>20</' "$case_dir/baseline.props" > "$MOCK_EARLY_VERSION_FILE"
    expect_failure 'Version.props changed during the build' both
    cmp -s "$version_file" "$MOCK_EARLY_VERSION_FILE" || fail_test 'Early source edit was overwritten'
    local target
    for target in ios android; do
        assert_arg "$FIXTURE/publish-$target.args" '-p:ApplicationDisplayVersion=1.0.1'
        assert_arg "$FIXTURE/publish-$target.args" '-p:ApplicationVersion=4'
    done
    assert_clean
    assert_no_release
}

test_baseline_trailing_newlines() {
    new_fixture
    printf '\n\n\n' >> "$version_file"
    invoke both
    assert_release 1.0.1 4 'ios android'
    jq -e '.previousVersion == "1.0.0" and .previousBuild == 3' \
        "$MOCK_OUTPUT/1.0.1-4/release.json" >/dev/null
}

test_finalize_failure() {
    new_fixture
    export MOCK_FAIL_FINALIZE=staging
    expect_failure 'fixture: staging rename failed' both
    assert_baseline
    assert_no_release
    new_fixture
    export MOCK_FAIL_FINALIZE=version
    expect_failure 'fixture: version rename failed' both
    assert_baseline
    assert_file "$MOCK_OUTPUT/1.0.1-4/release.json"
    assert_file "$MOCK_OUTPUT/1.0.1-4/ios/Fixture App.ipa"
    assert_file "$MOCK_OUTPUT/1.0.1-4/android/Fixture App-signed.aab"
    assert_absent "$MOCK_OUTPUT/1.0.1-4/COMPLETE"
    assert_contains "$capture" 'Incomplete finalization'
    unset MOCK_FAIL_FINALIZE
    expect_failure 'Unfinished release' android --build-number 5
    assert_baseline
    assert_absent "$MOCK_OUTPUT/1.0.1-5"
}

test_incomplete_release() {
    new_fixture
    mkdir -p "$MOCK_OUTPUT/0.9.0-2"
    printf '{"fixture":"incomplete"}\n' > "$MOCK_OUTPUT/0.9.0-2/release.json"
    expect_failure 'Unfinished release' both
    assert_baseline
    assert_contains "$MOCK_OUTPUT/0.9.0-2/release.json" '{"fixture":"incomplete"}'
    assert_not_contains "$FIXTURE/events" 'dotnet:publish'
}

test_output_collisions() {
    new_fixture
    mkdir -p "$MOCK_OUTPUT/1.0.1-4"
    printf 'existing release\n' > "$MOCK_OUTPUT/1.0.1-4/existing"
    expect_failure 'Output already exists' both
    assert_baseline
    assert_contains "$MOCK_OUTPUT/1.0.1-4/existing" 'existing release'
    assert_not_contains "$FIXTURE/events" 'dotnet:publish'
    new_fixture
    mkdir -p "$MOCK_OUTPUT"
    printf 'existing file\n' > "$MOCK_OUTPUT/1.0.1-4"
    expect_failure 'Output already exists' both
    assert_baseline
    assert_contains "$MOCK_OUTPUT/1.0.1-4" 'existing file'
    new_fixture
    export MOCK_OUTPUT_COLLISION=ios
    expect_failure 'Output appeared during the build' ios
    assert_baseline
    assert_contains "$MOCK_OUTPUT/1.0.1-4/external" 'do not overwrite'
    assert_absent "$MOCK_OUTPUT/1.0.1-4/COMPLETE"
    assert_absent "$MOCK_OUTPUT/1.0.1-4/ios"
}

test_sequential_build_only() {
    new_fixture
    invoke ios
    assert_release 1.0.1 4 ios
    cp "$MOCK_OUTPUT/1.0.1-4/release.json" "$case_dir/first-manifest.json"
    invoke android --bump none
    assert_release 1.0.1 5 android
    assert_absent "$MOCK_OUTPUT/1.0.1-5/ios"
    assert_absent "$MOCK_OUTPUT/1.0.1-4/android"
    cmp -s "$MOCK_OUTPUT/1.0.1-4/release.json" "$case_dir/first-manifest.json" || fail_test 'Earlier release modified'
    jq -e '.previousVersion == "1.0.1" and .previousBuild == 4 and .gitDirty == true' \
        "$MOCK_OUTPUT/1.0.1-5/release.json" >/dev/null
}

run_test() {
    local name="$1" result
    case_dir="$suite_dir/$name"
    fixture_number=0
    mkdir "$case_dir"
    set +e
    (set -e; "$name")
    result=$?
    set -e
    if [[ $result -eq 0 ]]; then
        passed=$((passed + 1))
        printf 'PASS %s\n' "$name"
    else
        failed=$((failed + 1))
        printf 'FAIL %s\n' "$name" >&2
        if [[ -f "$case_dir/output.log" ]]; then tail -n 20 "$case_dir/output.log" >&2; fi
    fi
}

for test in test_both test_selections test_bumps test_overrides test_invalid_options test_overflow \
    test_dry_run_help test_output_paths test_publish_failures_retry test_artifact_failures \
    test_verifier_failures test_preflight_failures test_bundletool_discovery test_existing_lock test_concurrent_lock \
    test_source_edit test_early_source_edit test_baseline_trailing_newlines \
    test_finalize_failure test_incomplete_release test_output_collisions \
    test_sequential_build_only; do
    run_test "$test"
done
printf '\nRelease mobile regressions: %s passed, %s failed.\n' "$passed" "$failed"
[[ $failed -eq 0 ]]
