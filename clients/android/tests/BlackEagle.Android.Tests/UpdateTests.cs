using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BlackEagle.Android.Updates;
using Xunit;

namespace BlackEagle.Android.Tests;

public sealed class UpdateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Apk = "test APK content"u8.ToArray();

    /// <summary>Создает согласованное описание тестового выпуска.</summary>
    private static UpdateRelease Release() => new(1, "android", "arm64", "com.blackeaglevpn.client", 14, "0.5.4",
        "https://github.com/kazievnadir-max/blackeagle-release/releases/download/android-v0.5.4/BlackEagleVPN-0.5.4-arm64.apk",
        Apk.Length, Convert.ToHexString(SHA256.HashData(Apk)), "Обновление");

    /// <summary>Подписывает исходные байты метаданных отдельным тестовым ключом.</summary>
    private static byte[] Sign(RSA key, UpdateRelease release)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(release, Json);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate(Convert.ToBase64String(payload),
            Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))), Json);
    }

    /// <summary>Не допускает замену доверенного ключа или изменение подписанных метаданных.</summary>
    [Fact]
    public void SignatureRejectsTamperingAndWrongKey()
    {
        using var key = RSA.Create(2048); using var other = RSA.Create(2048);
        using var http = new HttpClient(); var client = new UpdateClient(http, key.ExportSubjectPublicKeyInfoPem());
        var signed = Sign(key, Release());
        Assert.Equal(Release(), client.Verify(signed, "arm64"));
        Assert.Throws<InvalidDataException>(() => client.Verify(Sign(other, Release()), "arm64"));
        var envelope = JsonSerializer.Deserialize<SignedUpdate>(signed, Json)!;
        var modified = envelope with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Release() with { VersionCode = 999 }, Json)) };
        Assert.Throws<InvalidDataException>(() => client.Verify(JsonSerializer.SerializeToUtf8Bytes(modified, Json), "arm64"));
    }

    /// <summary>Отклоняет подписанные, но несовместимые или опасные параметры обновления.</summary>
    [Fact]
    public void MetadataRejectsInvalidRelease()
    {
        using var key = RSA.Create(2048); using var http = new HttpClient();
        var client = new UpdateClient(http, key.ExportSubjectPublicKeyInfoPem());
        var release = Release();
        UpdateRelease[] invalid = [release with { Schema = 2 }, release with { Platform = "windows" },
            release with { Architecture = "x64" }, release with { PackageId = "wrong.package" },
            release with { Size = 0 }, release with { Size = UpdateClient.MaxApkSize + 1 },
            release with { Sha256 = new string('z', 64) }, release with { VersionCode = 0 },
            release with { Url = release.Url.Replace("https:", "http:") },
            release with { Url = release.Url.Replace("github.com/", "github.com.evil.example/") },
            release with { Url = release.Url.Replace("blackeagle-release/", "other-repo/") }];
        foreach (var item in invalid) Assert.Throws<InvalidDataException>(() => client.Verify(Sign(key, item), "arm64"));
    }

    /// <summary>Отличает новый выпуск от отката, отсутствующего канала и ошибки GitHub.</summary>
    [Fact]
    public async Task CheckHandlesVersionsAndHttpFailures()
    {
        using var key = RSA.Create(2048);
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Sign(key, Release())) }));
        var client = new UpdateClient(http, key.ExportSubjectPublicKeyInfoPem());
        Assert.NotNull(await client.CheckAsync("arm64", 13, default));
        Assert.Null(await client.CheckAsync("arm64", 14, default));
        Assert.Null(await client.CheckAsync("arm64", 15, default));
        using var missing = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateClient(missing, key.ExportSubjectPublicKeyInfoPem()).CheckAsync("arm64", 1, default));
        using var failure = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateClient(failure, key.ExportSubjectPublicKeyInfoPem()).CheckAsync("arm64", 1, default));
    }

    /// <summary>Проверяет целостность APK, очистку незавершенных загрузок и возможность повторной загрузки.</summary>
    [Fact]
    public async Task DownloadValidatesAndRetries()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "update.apk");
        try
        {
            byte[] response = "broken payload!!"u8.ToArray();
            using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response) }));
            var client = new UpdateClient(http, "unused");
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(Release(), path, null, default));
            Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".part"));
            response = Apk;
            await client.DownloadAsync(Release(), path, null, default);
            Assert.Equal(Apk, await File.ReadAllBytesAsync(path));
            await File.WriteAllBytesAsync(path, new byte[Apk.Length]);
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateClient.ValidateFileAsync(Release(), path, default));
            using var ct = new CancellationTokenSource(); ct.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(Release(), path, null, ct.Token));
            Assert.False(File.Exists(path + ".part"));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Отклоняет перенаправление загрузки на HTTP или посторонний домен.</summary>
    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://evil.example/file")]
    public async Task RejectsUntrustedRedirect(string url)
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(url); return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateClient(http, "unused").DownloadAsync(Release(), Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".apk"), null, default));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        /// <summary>Подменяет сеть для воспроизводимых проверок протокола обновлений.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
