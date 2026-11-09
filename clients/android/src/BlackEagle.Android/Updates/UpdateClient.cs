using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackEagle.Android.Updates;

internal sealed record UpdateRelease(int Schema, string Platform, string Architecture, string PackageId,
    long VersionCode, string Version, string Url, long Size, string Sha256, string Notes);
internal sealed record SignedUpdate(string Payload, string Signature);

[JsonSerializable(typeof(UpdateRelease))]
[JsonSerializable(typeof(SignedUpdate))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class UpdateJsonContext : JsonSerializerContext { }

internal sealed class UpdateClient(HttpClient http, string publicKey)
{
    internal const string Repository = "kazievnadir-max/blackeagle-release";
    internal const long MaxApkSize = 300L * 1024 * 1024;

    /// <summary>Получает подписанный канал Android и возвращает только более новую совместимую версию.</summary>
    internal async Task<UpdateRelease?> CheckAsync(string architecture, long installedVersion, CancellationToken ct)
    {
        if (architecture is not ("arm64" or "x64")) throw new InvalidDataException("Unsupported architecture.");
        var url = new Uri($"https://raw.githubusercontent.com/{Repository}/main/channels/android-{architecture}.json");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        // Отсутствие канала не подтверждает актуальность установленной версии.
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(stream, buffer, 64 * 1024, null, ct);
        var release = Verify(buffer.ToArray(), architecture);
        return release.VersionCode > installedVersion ? release : null;
    }

    /// <summary>Проверяет подпись исходных байтов описания, платформу и допустимые параметры файла.</summary>
    internal UpdateRelease Verify(byte[] envelopeBytes, string architecture)
    {
        if (envelopeBytes.Length > 64 * 1024) throw new InvalidDataException("Manifest is too large.");
        var envelope = JsonSerializer.Deserialize(envelopeBytes, UpdateJsonContext.Default.SignedUpdate) ?? throw new InvalidDataException();
        if (string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature)) throw new InvalidDataException();
        var payload = Convert.FromBase64String(envelope.Payload);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("Invalid update signature.");
        var release = JsonSerializer.Deserialize(payload, UpdateJsonContext.Default.UpdateRelease) ?? throw new InvalidDataException();
        if (release.Schema != 1 || release.Platform != "android" || release.Architecture != architecture ||
            release.PackageId != "com.blackeaglevpn.client" || release.VersionCode <= 0 ||
            string.IsNullOrWhiteSpace(release.Version) || release.Version.Length > 40 ||
            release.Version.Any(char.IsControl) || release.Notes is null || release.Notes.Length > 4000 ||
            release.Size is <= 0 or > MaxApkSize || release.Sha256 is not { Length: 64 } ||
            !release.Sha256.All(Uri.IsHexDigit) || !IsAssetUrl(release.Url))
            throw new InvalidDataException("Invalid update metadata.");
        return release;
    }

    /// <summary>Разрешает APK только из выпусков официального репозитория по HTTPS.</summary>
    internal static bool IsAssetUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.Host == "github.com" && uri.UserInfo == "" &&
        uri.Query == "" && uri.Fragment == "" &&
        uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal) &&
        uri.AbsolutePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Скачивает APK с ограничением размера и публикует локальный файл только после проверки SHA-256.</summary>
    internal async Task DownloadAsync(UpdateRelease release, string destination, IProgress<int>? progress, CancellationToken ct)
    {
        if (!IsAssetUrl(release.Url) || release.Size is <= 0 or > MaxApkSize) throw new InvalidDataException();
        var part = destination + ".part";
        try
        {
            using var response = await GetAssetAsync(new Uri(release.Url), ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } size && size != release.Size) throw new InvalidDataException("Unexpected size.");
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await CopyLimitedAsync(source, target, release.Size, progress, ct);
                if (target.Length != release.Size) throw new InvalidDataException("Incomplete APK.");
            }
            await ValidateFileAsync(release, part, ct);
            File.Move(part, destination, true);
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    /// <summary>Повторно проверяет файл перед передачей установщику, включая возврат из настроек разрешений.</summary>
    internal static async Task ValidateFileAsync(UpdateRelease release, string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length != release.Size) throw new InvalidDataException("Unexpected APK size.");
        var hash = await SHA256.HashDataAsync(stream, ct);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(release.Sha256)))
            throw new InvalidDataException("Invalid APK checksum.");
    }

    /// <summary>Следует ограниченной цепочке перенаправлений GitHub без перехода на HTTP или посторонний сервер.</summary>
    private async Task<HttpResponseMessage> GetAssetAsync(Uri uri, CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("Missing redirect.");
            uri = new Uri(uri, location);
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo != "" || uri.Fragment != "" ||
                !(IsAssetUrl(uri.AbsoluteUri) || uri.Host == "release-assets.githubusercontent.com"))
                throw new InvalidDataException("Untrusted download redirect.");
        }
        throw new InvalidDataException("Too many redirects.");
    }

    /// <summary>Копирует поток с жестким пределом размера и сообщает процент загрузки.</summary>
    private static async Task CopyLimitedAsync(Stream source, Stream target, long limit, IProgress<int>? progress, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; var previous = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("Download exceeds size limit.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            var percent = (int)(total * 100 / limit);
            if (percent != previous) { progress?.Report(percent); previous = percent; }
        }
    }
}
