using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Android.Content;
using Android.Content.PM;
using Avalonia.Threading;

namespace BlackEagle.Android.Updates;

internal sealed class AndroidUpdates
{
    internal static AndroidUpdates Instance { get; } = new();
    private readonly UpdateClient client;
    private CancellationTokenSource? cancellation;
    private DateTimeOffset lastCheck;
    private string? downloaded;
    internal UpdateRelease? Release { get; private set; }
    internal string State { get; private set; } = "idle";
    internal int Percent { get; private set; }
    internal bool Busy => cancellation is not null;
    internal event Action? Changed;
    private static global::Android.Content.Context Context => global::Android.App.Application.Context;
    private static long InstalledVersion => Context.PackageManager!.GetPackageInfo(Context.PackageName!, 0)!.LongVersionCode;

    /// <summary>Создает загрузчик с открытым ключом выпуска и отключенными автоматическими перенаправлениями.</summary>
    private AndroidUpdates()
    {
        using var key = typeof(AndroidUpdates).Assembly.GetManifestResourceStream("BlackEagle.UpdatePublicKey")!;
        using var reader = new StreamReader(key);
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlackEagle-Android-Updater/1");
        client = new UpdateClient(http, reader.ReadToEnd());
    }

    /// <summary>Проверяет канал и кратко сообщает об актуальной версии только после ручного запроса.</summary>
    internal async Task CheckAsync(bool manual)
    {
        if (Busy || !manual && DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromHours(12)) return;
        lastCheck = DateTimeOffset.UtcNow;
        cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        State = "checking"; Notify();
        try
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X64 => "x64", _ => "unsupported" };
            Release = await client.CheckAsync(architecture, InstalledVersion, cancellation.Token);
            downloaded = null;
            State = Release is null ? "current" : "available";
            if (manual && Release is null)
                global::Android.Widget.Toast.MakeText(Context,
                    MobilePreferences.IsEnglish ? "The latest version is installed." : "Установлена актуальная версия программы",
                    global::Android.Widget.ToastLength.Long)?.Show();
        }
        catch (OperationCanceledException) { State = "check-error"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or FormatException or CryptographicException)
        { State = "check-error"; }
        finally { cancellation.Dispose(); cancellation = null; Notify(); }
    }

    /// <summary>Загружает проверенный APK по запросу пользователя и открывает системный установщик.</summary>
    internal async Task DownloadOrInstallAsync()
    {
        if (Busy || Release is not { } release) return;
        cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cancellation.Token;
        try
        {
            if (downloaded is null || !File.Exists(downloaded))
            {
                var directory = System.IO.Path.Combine(Context.CacheDir!.AbsolutePath, "updates");
                Directory.CreateDirectory(directory);
                var target = System.IO.Path.Combine(directory, "update.apk");
                State = "downloading"; Percent = 0; Notify();
                await client.DownloadAsync(release, target, new Progress<int>(value => { Percent = value; Notify(); }), ct);
                downloaded = target;
            }
            State = "verifying"; Notify();
            await UpdateClient.ValidateFileAsync(release, downloaded, ct);
            VerifyPackage(downloaded, release);
            if (!Context.PackageManager!.CanRequestPackageInstalls())
            {
                var settings = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                    global::Android.Net.Uri.Parse("package:" + Context.PackageName));
                settings.AddFlags(ActivityFlags.NewTask);
                Context.StartActivity(settings);
                State = "permission";
                return; // После возврата пользователь повторно нажимает «Установить».
            }
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(Context, Context.PackageName + ".updates", new Java.IO.File(downloaded));
            var install = new Intent(Intent.ActionView);
            install.SetDataAndType(uri, "application/vnd.android.package-archive");
            install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            install.ClipData = ClipData.NewRawUri("APK", uri);
            Context.StartActivity(install);
            State = "installer";
        }
        catch (OperationCanceledException) { State = "cancelled"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or CryptographicException or Java.Lang.Exception or InvalidOperationException)
        { downloaded = null; State = "download-error"; }
        finally { cancellation.Dispose(); cancellation = null; Notify(); }
    }

    /// <summary>Проверяет идентификатор, версию и совпадение сертификатов APK с установленным приложением.</summary>
    private static void VerifyPackage(string path, UpdateRelease release)
    {
        var manager = Context.PackageManager!;
        using var apk = manager.GetPackageArchiveInfo(path, PackageInfoFlags.SigningCertificates);
        using var installed = manager.GetPackageInfo(Context.PackageName!, PackageInfoFlags.SigningCertificates)!;
        if (apk is null || apk.PackageName != Context.PackageName || apk.LongVersionCode != release.VersionCode ||
            apk.VersionName != release.Version || apk.LongVersionCode <= installed.LongVersionCode)
            throw new InvalidDataException("APK package or version mismatch.");
        var expected = Certificates(installed);
        if (expected.Count == 0 || !expected.SetEquals(Certificates(apk)))
            throw new InvalidDataException("APK signing certificate mismatch.");
    }

    /// <summary>Получает SHA-256 текущих сертификатов подписи без разрешения произвольной смены ключа.</summary>
    private static HashSet<string> Certificates(PackageInfo package) =>
        (package.SigningInfo?.GetApkContentsSigners() ?? []).Select(signature =>
            Convert.ToHexString(SHA256.HashData(signature.ToByteArray()!))).ToHashSet(StringComparer.Ordinal);

    /// <summary>Отменяет загрузку или проверку по запросу пользователя.</summary>
    internal void Cancel() => cancellation?.Cancel();

    /// <summary>Уведомляет видимый интерфейс об изменении состояния на основном потоке.</summary>
    private void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());
}
