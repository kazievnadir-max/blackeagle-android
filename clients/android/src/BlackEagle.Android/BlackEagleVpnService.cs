using System.Net;
using System.Net.Sockets;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using BlackEagle.Client.Core;

namespace BlackEagle.Android;

[Service(Name = "com.blackeaglevpn.client.VpnService", Permission = "android.permission.BIND_VPN_SERVICE",
    Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[IntentFilter(["android.net.VpnService"])]
public sealed class BlackEagleVpnService : VpnService
{
    private const string Channel = "vpn";
    private const int NotificationId = 21;
    private readonly object sync = new();
    private CancellationTokenSource? lifetime;
    private AwgNative.ProtectSocket? protectCallback;
    private bool active;
    private ParcelFileDescriptor? tunnelDescriptor;
    private string? engineConfiguration;
    private long nextRecoveryAt;
    private int recoveryAttempts;
    internal const string ConnectAction = "com.blackeaglevpn.client.CONNECT";
    private CancellationTokenSource? backgroundConnection;
    private Task backgroundTask = Task.CompletedTask;

    /// <summary>Публикует уведомление до продолжения запуска и регистрирует службу в адаптере.</summary>
    public override void OnCreate()
    {
        base.OnCreate();
        ShowForegroundNotification();
        AndroidTunnel.Instance.Attach(this);
    }

    /// <summary>Не восстанавливает VPN по устаревшей конфигурации после завершения процесса.</summary>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ConnectAction && backgroundConnection is null && AndroidTunnel.Instance.State == "disconnected")
        {
            backgroundConnection = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            AndroidTunnel.Instance.Publish("connecting", "Подключение…");
            backgroundTask = ConnectFromTileAsync(backgroundConnection.Token);
        }
        return StartCommandResult.NotSticky;
    }

    /// <summary>Отменяет запрос плитки до ожидания освобождения платформенного адаптера.</summary>
    internal void CancelBackgroundConnection() => backgroundConnection?.Cancel();

    /// <summary>Ожидает завершения отмененного фонового запроса перед новым подключением.</summary>
    internal async Task StopBackgroundConnectionAsync(CancellationToken ct)
    {
        CancelBackgroundConnection();
        await backgroundTask.WaitAsync(ct);
    }

    /// <summary>Проверяет действующие права и подключает сохраненный сервер без создания Activity.</summary>
    private async Task ConnectFromTileAsync(CancellationToken ct)
    {
        try
        {
            var store = new AndroidSessionStore();
            var origin = new System.Uri("https://blackeagle-vpn.com/");
            using var api = new ClientAccessClient(origin, store);
            using var vpn = new VpnClient(origin, store, store, AndroidTunnel.Instance, devicePlatform: "android",
                adblockEnabled: () => MobilePreferences.AdBlockingEnabled);
            var access = await api.RefreshAsync(ct);
            var last = await vpn.LastServerAsync(ct);
            var selected = Guid.TryParse(MobilePreferences.SelectedServer, out var id) ? id : last;
            var server = access.Servers.FirstOrDefault(s => s.Id == selected) ?? access.Servers.FirstOrDefault();
            if (access.RequiresActivation || server is null) throw new InvalidOperationException();
            await vpn.ConnectAsync(server.Id, ct);
        }
        catch (Exception)
        {
            CloseTunnel();
            if (!ct.IsCancellationRequested)
            {
                using var handler = new Handler(Looper.MainLooper!);
                handler.Post(() => global::Android.Widget.Toast.MakeText(this,
                    MobilePreferences.IsEnglish ? "Connection failed. Check your profile and network in Black Eagle VPN."
                        : "Не удалось подключиться. Проверьте профиль и сеть в Black Eagle VPN.",
                    global::Android.Widget.ToastLength.Long)!.Show());
            }
            StopSelf();
        }
        finally { backgroundConnection?.Dispose(); backgroundConnection = null; }
    }

    /// <summary>Создает постоянное уведомление с переходом в экран отключения VPN.</summary>
    private void ShowForegroundNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Black Eagle VPN", NotificationImportance.Low));
        var launch = new Intent(this, typeof(MainActivity));
        var pending = PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var builder = new Notification.Builder(this, Channel);
        var notification = builder.SetContentTitle("Black Eagle VPN")!.SetContentText(MobilePreferences.IsEnglish ? "Manage connection" : "Управление подключением")!
            .SetSmallIcon(Resource.Drawable.eagle_head_black)!.SetOngoing(true)!.SetContentIntent(pending)!.Build();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else StartForeground(NotificationId, notification);
    }

    /// <summary>Разрешает endpoint до создания маршрутов и поднимает TUN с защищенными UDP-сокетами.</summary>
    internal async Task ConnectAsync(TunnelRequest request, CancellationToken ct)
    {
        var c = request.Configuration;
        var addresses = await Dns.GetHostAddressesAsync(c.Endpoint.Split(':')[0], ct);
        var endpoint = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new IOException("У сервера нет IPv4 адреса.");
        var configuration = AndroidAwgConfiguration.Build(request, endpoint);
        CancellationToken serviceToken;
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (active) throw new InvalidOperationException("VPN уже подключен.");
            using var builder = new Builder(this);
            builder.SetSession("Black Eagle VPN")!.SetMtu(c.Mtu)!.AddAddress(c.Address.Split('/')[0], 32)!
                .AddRoute("0.0.0.0", 0)!.AddDnsServer(c.Dns)!.SetBlocking(true);
            SplitTunnelPreferences.Apply(builder);
            // No IPv6 address or AllowFamily(AF_INET6): Android blocks IPv6 for this VPN.
            var descriptor = builder.Establish() ?? throw new IOException("Разрешение VPN отозвано.");
            protectCallback = ProtectTransportSocket;
            try
            {
                var result = AwgNative.Start(descriptor.Fd, configuration, protectCallback);
                if (result != 0) throw new IOException($"Ошибка запуска AWG ({result}).");
            }
            catch
            {
                try { CloseDescriptor(descriptor); }
                finally { protectCallback = null; }
                throw;
            }
            // Нативный движок владеет дубликатом FD; оригинал удерживает маршруты при его перезапуске.
            tunnelDescriptor = descriptor;
            engineConfiguration = configuration;
            recoveryAttempts = 0;
            nextRecoveryAt = 0;
            active = true;
            lifetime = new CancellationTokenSource();
            serviceToken = lifetime.Token;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, serviceToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            while (AwgNative.Handshake() <= 0) await Task.Delay(300, timeout.Token);
            lock (sync)
            {
                timeout.Token.ThrowIfCancellationRequested();
                AndroidTunnel.Instance.PublishDns(c.Dns, c.AdblockEnabled == true);
                AndroidTunnel.Instance.Publish("connected", "Подключено");
            }
            _ = MonitorAsync(serviceToken);
        }
        catch { CloseTunnel(); throw; }
    }

    /// <summary>Исключает транспорт AWG из VPN при каждом открытии или пересоздании сокета.</summary>
    private int ProtectTransportSocket(int fd)
    {
        try { return Protect(fd) ? 1 : 0; }
        catch { return 0; }
    }

    /// <summary>Восстанавливает AWG при устаревшем handshake, сохраняя TUN и ограничивая частоту повторов.</summary>
    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);
                lock (sync)
                {
                    ct.ThrowIfCancellationRequested();
                    var seconds = AwgNative.Handshake();
                    var fresh = TunnelRecoveryPolicy.IsFresh(seconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    AndroidTunnel.Instance.Publish(fresh ? "connected" : "reconnecting", fresh ? "Подключено" : "Восстановление соединения…");
                    if (fresh)
                    {
                        if (recoveryAttempts > 0)
                            global::Android.Util.Log.Info("BlackEagleVpn", "Tunnel handshake recovered.");
                        recoveryAttempts = 0;
                        nextRecoveryAt = 0;
                    }
                    else if (active && SystemClock.ElapsedRealtime() >= nextRecoveryAt)
                    {
                        RestartEngine();
                    }
                }
            }
        }
        catch (System.OperationCanceledException) { }
        catch (Exception ex)
        {
            // Не записываем конфигурацию, адреса, ключи или текст исключения.
            global::Android.Util.Log.Error("BlackEagleVpn", $"Tunnel monitor stopped: {ex.GetType().Name}");
            lock (sync)
            {
                if (!ct.IsCancellationRequested) CloseTunnel();
            }
        }
    }

    /// <summary>Пересоздает защищенные UDP-сокеты и handshake, удерживая исходный дескриптор TUN.</summary>
    private void RestartEngine()
    {
        if (tunnelDescriptor is null || engineConfiguration is null || protectCallback is null) return;
        recoveryAttempts = Math.Min(recoveryAttempts + 1, 3);
        nextRecoveryAt = SystemClock.ElapsedRealtime() + TunnelRecoveryPolicy.RetryDelayMilliseconds(recoveryAttempts);
        global::Android.Util.Log.Warn("BlackEagleVpn", "Stale handshake; restarting AWG while retaining VPN routes.");
        try
        {
            AwgNative.Stop();
            var result = AwgNative.Start(tunnelDescriptor.Fd, engineConfiguration, protectCallback);
            if (result != 0)
                global::Android.Util.Log.Warn("BlackEagleVpn", $"AWG restart failed: {result}; retry scheduled.");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("BlackEagleVpn", $"AWG restart failed: {ex.GetType().Name}; retry scheduled.");
        }
    }

    /// <summary>Закрывает AWG и TUN, после чего Android восстанавливает обычные маршруты и DNS.</summary>
    internal void CloseTunnel()
    {
        lock (sync)
        {
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            try { if (active) AwgNative.Stop(); }
            finally
            {
                var descriptor = tunnelDescriptor;
                tunnelDescriptor = null;
                try { CloseDescriptor(descriptor); }
                finally
                {
                    engineConfiguration = null;
                    recoveryAttempts = 0; nextRecoveryAt = 0;
                    active = false; protectCallback = null;
                }
            }
        }
        AndroidTunnel.Instance.Publish("disconnected", "Отключено");
    }

    /// <summary>Закрывает системный TUN-дескриптор до освобождения JNI-обертки, не ожидая сборщика мусора.</summary>
    private static void CloseDescriptor(ParcelFileDescriptor? descriptor)
    {
        if (descriptor is null) return;
        try { descriptor.Close(); }
        finally { descriptor.Dispose(); }
    }

    /// <summary>Останавливает туннель при отзыве разрешения или выборе другого VPN.</summary>
    public override void OnRevoke() { CloseTunnel(); StopSelf(); base.OnRevoke(); }

    /// <summary>Освобождает ресурсы при завершении службы операционной системой.</summary>
    public override void OnDestroy()
    {
        CancelBackgroundConnection();
        CloseTunnel();
        AndroidTunnel.Instance.Detach(this);
        base.OnDestroy();
    }
}
