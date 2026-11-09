# Black Eagle VPN for Android

Public source repository: https://github.com/kazievnadir-max/blackeagle-android-

Android 11+ client built with C#/.NET 10 and Avalonia UI, using AmneziaWG.
Access requires a Black Eagle VPN subscription or trial activated through the
Telegram bot https://t.me/BlackEagleVPNbot. The API endpoint is
https://blackeagle-vpn.com/.

This source export includes the Android client, shared client logic, UI library
and protocol contracts. The directory layout preserves existing project references.
It does not include the control plane, bot, node deployments or server secrets.

## Build

Install .NET SDK 10.0.401 with Android workload 36.1.2, Android SDK/API 36,
build-tools 36.0.0, OpenJDK 17, Android NDK 28.2.13676358 and Go 1.27.1.
Set ANDROID_SDK_ROOT,
ANDROID_NDK_HOME and JAVA_HOME to their local installation paths.

```sh
bash clients/android/deploy/build-release.sh
```

The native build script builds the AWG shared libraries and copies their upstream
license into the APK assets. Never commit signing keys or credentials.

The default release target is ARM64. The unsigned APK is located at
`clients/android/src/BlackEagle.Android/bin/Release/net10.0-android/android-arm64/com.blackeaglevpn.client.apk`.
This is not a production-signed release. Set `ANDROID_RUNTIME=android-x64` to
build for x86_64 (not yet validated). `DOTNET_CMD` and `GO_CMD` can override
tool executable paths. Do not disable NuGet signature checks to work around
an outdated system certificate store.

For version 0.5.1, a separate ARM64 APK has been signed using the project's
permanent RSA-4096 key. Its signing certificate fingerprint and checksum are
recorded in [release notes](docs/RELEASE-0.5.1.md). The local signing key and
password are stored outside this repository. The build script's automatic
`Signed.apk` output still uses SDK signing defaults; it is not the permanently
signed distribution artifact.

## Direct APK updates

Version 0.5.3 introduces signed update checks from the shared public repository
https://github.com/kazievnadir-max/blackeagle-release. Downloads start on user
request; Android confirms installation. See [Android update protocol and release
steps](docs/ANDROID-UPDATES.md). The first updater-enabled APK must be installed
manually. [Android 0.5.4](https://github.com/kazievnadir-max/blackeagle-release/releases/tag/android-v0.5.4)
is the first published release in this update repository.

The current release is [Android 0.5.5](https://github.com/kazievnadir-max/blackeagle-release/releases/tag/android-v0.5.5).
It fixes VPN shutdown from Quick Settings: the system TUN descriptor is explicitly
closed, so disconnecting no longer leaves a VPN interface blocking internet access.

## Licensing

Original client source in this repository is licensed under MIT; see LICENSE.
Third-party dependencies retain their own licenses. Their notices must be
preserved. The project owner confirmed MIT distribution rights for both eagle
images; see [artwork licensing](docs/ARTWORK-LICENSE.md). This does not relicense
third-party fonts or libraries.

## F-Droid preparation status

Not submitted or accepted into the main F-Droid repository yet. Outstanding:

See [FDROID-STATUS.md](FDROID-STATUS.md) for verified checks and remaining
inclusion blockers. The commands above are not yet a validated F-Droid
build recipe.

- Validate a clean build and all dependency licenses/provenance.
- Agree on .NET SDK/workload and NuGet dependency handling with maintainers.
- Prepare and test fdroiddata build metadata and store artwork/descriptions.
- Declare the fixed service endpoint and Telegram dependency as appropriate.
- Decide signing/reproducible-build strategy before the first public release.
- Create version tags after validating the release build.
