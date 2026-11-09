using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using BlackEagle.Client.UI;
using Path = Avalonia.Controls.Shapes.Path;
using Button = Avalonia.Controls.Button;

#if DESKTOP_SHARED
namespace BlackEagle.Client.UI;
#else
namespace BlackEagle.Android;
#endif

/// <summary>Единый мобильный интерфейс Android и Linux.</summary>
public sealed class MobileView : UserControl
{
    private readonly MainViewModel model;
    private readonly IMobileViewPlatform platform;
    private readonly ConnectionFeedback connectionFeedback = new();
    private string StatusMessage => Translate(connectionFeedback.Display(model.Message));
    private readonly HashSet<Guid> favorites = MobilePreferences.Favorites();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Grid root = new();
    private readonly ContentControl pageHost = new();
    private readonly ContentControl navigationHost = new();
    private readonly TextBlock elapsed = new() { FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
#if DESKTOP_SHARED
    private readonly Bitmap eagle = new(AssetLoader.Open(new Uri("avares://BlackEagle.Client.UI/Assets/home-eagle.png")));
#else
    private readonly Bitmap eagle = new(AssetLoader.Open(new Uri("avares://BlackEagle.Android/Assets/home-eagle.png")));
#endif
    private Grid? overlay;
#if !DESKTOP_SHARED
    private TopLevel? navigationRoot;
#endif
    private Button? importButton;
    private TextBlock? importMessage;
    private string page = "home";
    private string lastModelState = "", renderedPage = "";
    private bool onlyFavorites, queued, attached;
    private int ticks;
    private bool replacingProfile;
    private string? replacementError;
    private string importFeedback = "", profileFeedback = "";
    private DateTimeOffset? profileFeedbackUntil;
#if DESKTOP_SHARED
    private bool CanOpenProfile => model.CanSelectServer;
    private bool CanReplaceProfile => model.CanImport && !replacingProfile;
#else
    private bool CanOpenProfile => !model.IsBusy && !replacingProfile;
    private bool CanReplaceProfile => CanOpenProfile && !string.IsNullOrWhiteSpace(model.Link);
#endif
    private bool English => MobilePreferences.IsEnglish;
    private bool Light => ActualThemeVariant == ThemeVariant.Light;
    private IBrush ForegroundColor => Brush(Light ? "#15191E" : "#F3F4F6");
    private IBrush Muted => Brush(Light ? "#606975" : "#969DA7");
    private IBrush Surface => Brush(Light ? "#FFFFFF" : "#191D22");
    private IBrush Accent => Brush(Light ? "#138746" : "#53E293");

#if !DESKTOP_SHARED
    /// <summary>Создает Android-интерфейс с системными мобильными службами.</summary>
    public MobileView(MainViewModel model) : this(model, new AndroidMobileViewPlatform()) { }
#endif

    /// <summary>Создает общую мобильную навигацию с функциями текущей платформы.</summary>
    public MobileView(MainViewModel model, IMobileViewPlatform platform)
    {
        this.model = model;
        this.platform = platform;
        root.RowDefinitions = new RowDefinitions("*,Auto");
        root.Children.Add(pageHost);
        Grid.SetRow(navigationHost, 1); root.Children.Add(navigationHost);
        Content = root;
        timer.Tick += OnTick;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        ActualThemeVariantChanged += OnThemeChanged;
        ApplyTheme();
        RenderPage();
    }

    /// <summary>Возвращает строку на выбранном пользователем языке.</summary>
    private string T(string ru, string en) => English ? en : ru;

    /// <summary>Создает кисть для мобильной палитры.</summary>
    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));

    /// <summary>Создает текст с едиными параметрами читаемости.</summary>
    private TextBlock Text(string value, double size = 15, bool muted = false) => new()
    {
        Text = value, FontSize = size, Foreground = muted ? Muted : ForegroundColor,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Создает доступную кнопку с областью касания не меньше 48 пикселей.</summary>
    private Button Button(Control content, string label, Action action)
    {
        var button = new Button
        {
            Content = content, MinHeight = 48, MinWidth = 48, Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(15), Background = Surface, Foreground = ForegroundColor,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Рисует масштабируемую линейную пиктограмму без зависимости от шрифтов Android.</summary>
    private Control Icon(string name, IBrush? color = null, double size = 24)
    {
        var data = name switch
        {
            "plus" => "M12,4 L12,20 M4,12 L20,12",
            "power" => "M12,2 L12,11 M6,5 A9,9 0 1 0 18,5",
            "home" => "M3,11 L12,3 L21,11 M5,10 L5,21 L10,21 L10,15 L14,15 L14,21 L19,21 L19,10",
            "servers" => "M4,4 L20,4 L20,9 L4,9 Z M4,15 L20,15 L20,20 L4,20 Z M7,6 L9,6 M7,17 L9,17",
            "profile" => "M16,7 A4,4 0 1 1 8,7 A4,4 0 1 1 16,7 M4,22 L4,19 C4,12 20,12 20,19 L20,22",
            "telegram" => "M2,10 L22,3 L17,22 L11,16 L8,19 L8,13 L18,6 L10,14 Z",
            "ping" => "M3,19 A10,10 0 1 1 21,19 M12,14 L18,7 M3,14 L6,14 M18,14 L21,14 M12,4 L12,7",
            "star" => "M12,2 L15,8 L22,9 L17,14 L18,21 L12,18 L6,21 L7,14 L2,9 L9,8 Z",
            "back" => "M15,4 L7,12 L15,20",
            "next" => "M9,4 L17,12 L9,20",
            "refresh" => "M20,4 L20,10 L14,10 M20,10 A8,8 0 1 0 20,17",
            "settings" => "M10,2.5 L14,2.5 L14.5,4.3 L16.1,5 L17.8,4.1 L20.1,6.4 L19.2,8.1 L19.9,9.7 L21.5,10.2 L21.5,13.8 L19.9,14.3 L19.2,15.9 L20.1,17.6 L17.8,19.9 L16.1,19 L14.5,19.7 L14,21.5 L10,21.5 L9.5,19.7 L7.9,19 L6.2,19.9 L3.9,17.6 L4.8,15.9 L4.1,14.3 L2.5,13.8 L2.5,10.2 L4.1,9.7 L4.8,8.1 L3.9,6.4 L6.2,4.1 L7.9,5 L9.5,4.3 Z M15.25,12 A3.25,3.25 0 1 1 8.75,12 A3.25,3.25 0 1 1 15.25,12",
            _ => "M5,5 L19,19 M19,5 L5,19"
        };
        if (name is "power" or "profile" or "settings")
        {
            // Фиксированная система координат сохраняет отступ вокруг обводки.
            // Stretch.Uniform у Path нормализует границы геометрии и обрезает выступающий штрих.
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse(data), Stroke = color ?? ForegroundColor,
                StrokeThickness = name == "settings" ? 1.8 : 1.7,
                StrokeLineCap = PenLineCap.Round, Stretch = Stretch.None
            });
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }
        return new Viewbox { Width = size, Height = size, Child = new Path
        {
            Width = 24, Height = 24, Data = Geometry.Parse(data), Stroke = color ?? ForegroundColor,
            StrokeThickness = 1.7, StrokeLineCap = PenLineCap.Round, Stretch = Stretch.Uniform
        } };
    }

    /// <summary>Подписывает видимый экран на модель и запускает обновление длительности.</summary>
    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (attached) return;
        attached = true; model.PropertyChanged += ModelChanged; timer.Start(); QueueRender();
#if !DESKTOP_SHARED
        Updates.AndroidUpdates.Instance.Changed += QueueRender;
        _ = Updates.AndroidUpdates.Instance.CheckAsync(manual: false);
        navigationRoot = TopLevel.GetTopLevel(this);
        if (navigationRoot is not null) navigationRoot.BackRequested += OnBackRequested;
#endif
    }

    /// <summary>Останавливает таймер и подписку невидимого экрана, не отключая VPN.</summary>
    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        attached = false; model.PropertyChanged -= ModelChanged; timer.Stop();
#if !DESKTOP_SHARED
        Updates.AndroidUpdates.Instance.Changed -= QueueRender;
        if (navigationRoot is not null) navigationRoot.BackRequested -= OnBackRequested;
        navigationRoot = null;
#endif
    }

#if !DESKTOP_SHARED
    /// <summary>Закрывает диалог или возвращает на родительскую страницу при системном действии «Назад».</summary>
    private void OnBackRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (args.Handled) return;
        if (overlay is not null)
        {
            args.Handled = true;
            if (!replacingProfile) CloseImport();
            return;
        }
        if (page == "home") return;
        args.Handled = true;
        Navigate(page == "split-tunnel" ? "settings" : "home");
    }
#endif

    /// <summary>Перестраивает палитру при системном изменении темы.</summary>
    private void OnThemeChanged(object? sender, EventArgs args) => QueueRender();

    /// <summary>Обновляет состояние кнопок, не пересоздавая поле ввода ссылки при наборе текста.</summary>
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.Link) or nameof(MainViewModel.CanImport))
        { if (importButton is not null) importButton.IsEnabled = CanReplaceProfile; return; }
        var state = $"{model.Message}|{model.TunnelText}|{model.SelectedServer?.Id}|{model.SubscriptionStatus}|{model.SubscriptionDetail}|{model.AccountText}|{model.IsAdding}|{model.CanConnect}|{model.CanSelectServer}|{model.CanProbe}|{model.CanRefresh}|{model.IsBusy}|{string.Join(',', model.Servers.Select(s => s.Id))}";
        if (state == lastModelState) return;
        lastModelState = state;
        QueueRender();
    }

    /// <summary>Объединяет серию изменений модели в одно обновление мобильного экрана.</summary>
    private void QueueRender()
    {
        if (queued) return;
        queued = true;
        Dispatcher.UIThread.Post(() => { queued = false; RenderPage(); });
    }

    /// <summary>Обновляет монотонный таймер соединения и периодически проверяет срок подписки.</summary>
    private void OnTick(object? sender, EventArgs args)
    {
        UpdateElapsed();
        if (profileFeedbackUntil is { } until && DateTimeOffset.UtcNow >= until)
        {
            profileFeedback = ""; profileFeedbackUntil = null; QueueRender();
        }
        if (++ticks % 30 == 0) model.Tick();
    }

    /// <summary>Показывает время с первого handshake, включая восстановление транспортной сети.</summary>
    private void UpdateElapsed()
    {
        var connectedDuration = platform.ConnectedDuration;
        var span = connectedDuration ?? TimeSpan.Zero;
        elapsed.Text = $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
        elapsed.IsVisible = connectedDuration is not null;
        elapsed.Foreground = Accent;
    }

    /// <summary>Меняет страницу, сохраняя соединение и выбранный сервер.</summary>
    private void Navigate(string destination) { page = destination; RenderPage(); }

    /// <summary>Обновляет содержимое выбранной страницы и нижнюю навигацию.</summary>
    private void RenderPage()
    {
        root.Background = Brush(Light ? "#EEF1F4" : "#0D1013");
        var body = page switch
        {
            "servers" => ServersPage(), "profile" => ProfilePage(), "settings" => SettingsPage(),
#if !DESKTOP_SHARED
            "split-tunnel" => SplitTunnelPage(),
#endif
            _ => HomePage()
        };
        var offset = renderedPage == page && pageHost.Content is ScrollViewer previous ? previous.Offset : default;
        pageHost.Content = new ScrollViewer { Content = body, Offset = offset, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        renderedPage = page;
        navigationHost.Content = Navigation();
        if (overlay is not null)
        {
            if (!model.IsAdding) CloseImport();
            else
            {
                if (importMessage is not null) importMessage.Text = replacementError ?? importFeedback;
                if (importButton is not null) importButton.IsEnabled = CanReplaceProfile;
            }
        }
    }

    /// <summary>Создает верхнюю строку с названием страницы и действиями.</summary>
    private Grid Header(string title, Control right, Control? left = null)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 4, 0, 12) };
        if (left is not null) header.Children.Add(left);
        var label = Text(title, 24); label.FontWeight = FontWeight.SemiBold; label.Margin = new Thickness(left is null ? 0 : 10, 0);
        Grid.SetColumn(label, 1); header.Children.Add(label); Grid.SetColumn(right, 2); header.Children.Add(right);
        return header;
    }

    /// <summary>Создает контейнер страницы с безопасными отступами и ограничением ширины на планшете.</summary>
    private static StackPanel Page() => new() { Spacing = 14, Margin = new Thickness(20, 12, 20, 18), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>Рисует главный экран с эмблемой, кнопкой VPN и выбранной локацией.</summary>
    private Control HomePage()
    {
        var panel = Page();
        panel.Spacing = 9;
        var add = Button(Icon("plus"), T("Добавить профиль", "Add profile"), OpenImport);
        add.IsEnabled = model.CanLogout || (!model.HasServers && model.CanSelectServer);
#if !DESKTOP_SHARED
        add.IsEnabled = CanOpenProfile;
#endif
        var settings = Button(Icon("settings"), T("Настройки", "Settings"), () => Navigate("settings"));
        panel.Children.Add(Header("", settings, add));
#if !DESKTOP_SHARED
        if (Updates.AndroidUpdates.Instance.Release is { } update)
            panel.Children.Add(Button(Text(T($"Доступна версия {update.Version}", $"Version {update.Version} available")), T("Обновление", "Update"), () => Navigate("settings")));
#endif
        var hero = new Grid { Height = 138 };
        hero.Children.Add(new global::Avalonia.Controls.Image
        {
            Source = eagle, Height = 138, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(hero);
        var title = Text("Black Eagle VPN", 24); title.FontWeight = FontWeight.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(title);
        var tagline = Text(T("Свобода. Безопасность. Без границ.", "Freedom. Security. Without borders."), 13, true);
        tagline.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(tagline);
        var connected = platform.IsConnected;
        var power = Button(Icon("power", connected ? Brush("#D9FFEA") : ForegroundColor, 48), Translate(model.ConnectText), () => _ = ToggleConnectionAsync());
        power.Width = 136; power.Height = 136; power.CornerRadius = new CornerRadius(68);
        power.IsEnabled = model.CanConnect;
        power.Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(Color.Parse(connected ? "#20583E" : Light ? "#FAFBFC" : "#343A40"), 0), new(Color.Parse(connected ? "#071C13" : Light ? "#D6DDE4" : "#111417"), 1) } };
        var ring = new Border { Child = power, Padding = new Thickness(5), BorderThickness = new Thickness(connected ? 3 : 2),
            BorderBrush = connected ? Accent : Brush(Light ? "#B1BAC5" : "#565E66"), CornerRadius = new CornerRadius(80),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 17, 0, 3),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = connected ? 30 : 18, Spread = 2, Color = Color.Parse(connected ? "#5053E293" : "#302B323B") }) };
        panel.Children.Add(ring);
        var status = Text(Translate(model.TunnelText), 17); status.FontWeight = FontWeight.SemiBold;
        status.HorizontalAlignment = HorizontalAlignment.Center; if (connected) status.Foreground = Accent;
        panel.Children.Add(status);
        // TextBlock может иметь только одного визуального родителя.
        if (elapsed.Parent is Panel previous) previous.Children.Remove(elapsed);
        UpdateElapsed(); panel.Children.Add(elapsed);
        if (platform.ConnectedDuration is null)
        {
            var hint = Text(T("Нажмите, чтобы установить\nбезопасное соединение", "Tap to establish\na secure connection"), 14, true);
            hint.TextAlignment = TextAlignment.Center; panel.Children.Add(hint);
        }
        var server = model.SelectedServer;
        var selected = server is null ? Text(T("Выберите сервер", "Choose a server")) : ServerLabel(server);
        var selectedGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        selectedGrid.Children.Add(selected); var next = Icon("next", Muted, 18); Grid.SetColumn(next, 1); selectedGrid.Children.Add(next);
        var selector = Button(selectedGrid, T("Выбрать сервер", "Select server"), () => Navigate("servers"));
        selector.HorizontalContentAlignment = HorizontalAlignment.Stretch; selector.Padding = new Thickness(16); selector.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(selector);
        if (!string.IsNullOrEmpty(StatusMessage) && (!model.HasServers || model.Message.Contains("Не удалось") || model.Message.Contains("недоступ")))
            panel.Children.Add(Text(StatusMessage, 12, true));
        return panel;
    }

    /// <summary>Создает подпись реального сервера с флагом, названием и городом.</summary>
    private Control ServerLabel(ServerRow server)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*") };
        grid.Children.Add(new CountryFlag(server.CountryCode) { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 3 }; text.Children.Add(Text(server.Name, 15)); text.Children.Add(Text(server.City, 12, true));
        Grid.SetColumn(text, 1); grid.Children.Add(text); return grid;
    }

    /// <summary>Создает каталог с пингом, фильтром избранного и независимыми кнопками выбора и звезды.</summary>
    private Control ServersPage()
    {
        var panel = Page();
        var ping = Button(Icon("ping"), T("Измерить пинг", "Measure latency"), async () => { await model.ProbeAsync(); QueueRender(); }); ping.IsEnabled = model.CanProbe;
        panel.Children.Add(Header(T("Серверы", "Servers"), ping));
        var tabs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var all = Button(Text(T("Все", "All")), T("Все серверы", "All servers"), () => { onlyFavorites = false; RenderPage(); });
        var saved = Button(Text(T("☆  Избранные", "☆  Favorites")), T("Избранные", "Favorites"), () => { onlyFavorites = true; RenderPage(); });
        all.Background = onlyFavorites ? Brushes.Transparent : Surface; saved.Background = onlyFavorites ? Surface : Brushes.Transparent;
        tabs.Children.Add(all); Grid.SetColumn(saved, 1); tabs.Children.Add(saved); panel.Children.Add(tabs);
        var refresh = Button(Text(T("Обновить список", "Refresh list"), 13, true), T("Обновить список", "Refresh list"), () => _ = model.RefreshAsync());
        refresh.IsEnabled = model.CanRefresh; refresh.Background = Brushes.Transparent; panel.Children.Add(refresh);
        foreach (var server in model.Servers.Where(s => !onlyFavorites || favorites.Contains(s.Id)))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,48") };
            var select = Button(ServerLabel(server), server.Name, () => {
                model.SelectedServer = server;
#if !DESKTOP_SHARED
                MobilePreferences.Save("selected-server", server.Id.ToString());
#endif
                Navigate("home");
            });
            select.IsEnabled = model.CanSelectServer; select.HorizontalContentAlignment = HorizontalAlignment.Stretch; select.Background = Brushes.Transparent;
            row.Children.Add(select);
            var latency = Text(string.IsNullOrWhiteSpace(server.EndpointHost) ? T("Нет адреса", "No address") : Translate(server.PingText), 12, true);
            latency.Margin = new Thickness(3, 0); Grid.SetColumn(latency, 1); row.Children.Add(latency);
            var star = Button(Icon("star", favorites.Contains(server.Id) ? Accent : Muted, 20),
                favorites.Contains(server.Id) ? T("Убрать из избранного", "Remove favorite") : T("Добавить в избранное", "Add favorite"),
                () => { if (!favorites.Add(server.Id)) favorites.Remove(server.Id); MobilePreferences.SaveFavorites(favorites); RenderPage(); });
            star.Background = Brushes.Transparent; Grid.SetColumn(star, 2); row.Children.Add(star);
            panel.Children.Add(new Border { Child = row, CornerRadius = new CornerRadius(14), Background = Surface,
                BorderThickness = new Thickness(model.SelectedServer?.Id == server.Id ? 1 : 0), BorderBrush = Accent });
        }
        if (!model.HasServers) panel.Children.Add(Text(Translate(model.EmptyText), 14, true));
        else if (onlyFavorites && !model.Servers.Any(s => favorites.Contains(s.Id))) panel.Children.Add(Text(T("Добавьте серверы звёздочкой на вкладке «Все».", "Star a server in the All tab to save it here."), 14, true));
        panel.Children.Add(Text(StatusMessage, 12, true));
        return panel;
    }

    /// <summary>Показывает профиль и срок действующей подписки без отображения ключей и токенов.</summary>
    private Control ProfilePage()
    {
        var panel = Page();
        var add = Button(Icon("plus"), T("Добавить профиль", "Add profile"), OpenImport); add.IsEnabled = model.CanSelectServer;
#if !DESKTOP_SHARED
        add.IsEnabled = CanOpenProfile;
#endif
        panel.Children.Add(Header(T("Профиль", "Profile"), add));
        var details = new StackPanel { Spacing = 20 };
        details.Children.Add(Icon("profile", Accent, 48));
        details.Children.Add(Text(Translate(model.AccountText), 17));
        details.Children.Add(Text(Translate(model.SubscriptionStatus), 22));
        details.Children.Add(Text(Translate(model.SubscriptionDetail), 15, true));
        panel.Children.Add(new Border { Child = details, Padding = new Thickness(22), Background = Surface, CornerRadius = new CornerRadius(20) });
        var refresh = Button(Text(T("Обновить подписку", "Refresh subscription")), T("Обновить подписку", "Refresh subscription"), () => _ = RefreshProfileAsync());
        refresh.IsEnabled = model.CanRefresh; panel.Children.Add(refresh);
        panel.Children.Add(Button(Text(T("Управлять подпиской в Telegram", "Manage subscription in Telegram")), "Telegram", OpenTelegram));
        if (!string.IsNullOrEmpty(profileFeedback)) panel.Children.Add(Text(Translate(profileFeedback), 13, true));
        return panel;
    }

    /// <summary>Показывает результат ручного обновления профиля, скрывая подтверждение через пять секунд.</summary>
    private async Task RefreshProfileAsync()
    {
        if (!model.CanRefresh) return;
        profileFeedbackUntil = null;
        profileFeedback = "Обращаемся к сервису…";
        QueueRender();
        await model.RefreshAsync();
        profileFeedback = model.Message;
        if (profileFeedback == "Данные обновлены.") profileFeedbackUntil = DateTimeOffset.UtcNow.AddSeconds(5);
        QueueRender();
    }

    /// <summary>Создает страницу постоянных настроек языка, темы, блокировки рекламы и версии приложения.</summary>
    private Control SettingsPage()
    {
        var panel = Page();
        panel.Children.Add(Header(T("Настройки", "Settings"), new Border(), Button(Icon("back"), T("Назад", "Back"), () => Navigate("home"))));
        panel.Children.Add(Text(T("Тема оформления", "Appearance"), 17));
        var themes = new ComboBox { ItemsSource = new[] { T("Системная", "System"), T("Тёмная", "Dark"), T("Светлая", "Light") },
            SelectedIndex = MobilePreferences.Theme switch { "dark" => 1, "light" => 2, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 48 };
        themes.SelectionChanged += (_, _) => { MobilePreferences.Save("theme", themes.SelectedIndex switch { 1 => "dark", 2 => "light", _ => "system" }); ApplyTheme(); QueueRender(); };
        panel.Children.Add(themes);
        panel.Children.Add(Text(T("Язык", "Language"), 17));
        var languages = new ComboBox { ItemsSource = new[] { "Русский", "English" }, SelectedIndex = English ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 48 };
        languages.SelectionChanged += (_, _) => { MobilePreferences.Save("language", languages.SelectedIndex == 1 ? "en" : "ru"); QueueRender(); };
        panel.Children.Add(languages);
        var adblockTitle = T("Блокировка рекламы", "Ad blocking");
        var adblockRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var adblockLabel = Text(adblockTitle, 17);
#if DESKTOP_SHARED
        ToolTip.SetTip(adblockLabel, new TextBlock
        {
            Text = T("Блокирует рекламные и отслеживающие домены через DNS VPN-сервера. Изменение применяется при следующем подключении. Не блокирует всю рекламу, например встроенную рекламу YouTube.",
                "Blocks advertising and tracking domains through the VPN server's DNS. Changes apply on the next connection. Does not block all ads, such as embedded YouTube ads."),
            MaxWidth = 320, TextWrapping = TextWrapping.Wrap
        });
#endif
        adblockRow.Children.Add(adblockLabel);
        var adblock = new ToggleSwitch
        {
            IsChecked = MobilePreferences.AdBlockingEnabled,
            OnContent = null, OffContent = null,
            HorizontalAlignment = HorizontalAlignment.Right, MinHeight = 48
        };
        AutomationProperties.SetName(adblock, adblockTitle);
        var revertingAdblock = false;
        adblock.IsCheckedChanged += (_, _) =>
        {
            if (revertingAdblock) return;
            var enabled = adblock.IsChecked == true;
            if (!MobilePreferences.SaveAdBlocking(enabled))
            {
                revertingAdblock = true;
                adblock.IsChecked = !enabled;
                revertingAdblock = false;
                model.ReportMessage(T("Не удалось сохранить настройку.", "Could not save the setting."));
                return;
            }
        };
        Grid.SetColumn(adblock, 1);
        adblockRow.Children.Add(adblock);
        panel.Children.Add(adblockRow);
#if !DESKTOP_SHARED
        var splitTitle = T("Раздельное туннелирование", "Split tunneling");
        var splitLink = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        splitLink.Children.Add(Text(splitTitle, 17));
        var arrow = Text("›", 24); Grid.SetColumn(arrow, 1); splitLink.Children.Add(arrow);
        panel.Children.Add(Button(splitLink, splitTitle, () => Navigate("split-tunnel")));
#endif
        if (platform.SupportsAutostart)
        {
            var autostartTitle = T("Автозагрузка", "Autostart");
            var autostartRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            autostartRow.Children.Add(Text(autostartTitle, 17));
            var status = Text("", 12, true);
            status.IsVisible = false;
            var changing = false;
            var autostart = new ToggleSwitch
            {
                IsChecked = platform.IsAutostartEnabled,
                OnContent = null, OffContent = null,
                HorizontalAlignment = HorizontalAlignment.Right,
                MinHeight = 48
            };
            AutomationProperties.SetName(autostart, autostartTitle);
            autostart.IsCheckedChanged += (_, _) =>
            {
                if (changing) return;
                var enabled = autostart.IsChecked == true;
                if (platform.SetAutostartEnabled(enabled)) { status.Text = ""; status.IsVisible = false; return; }
                changing = true; autostart.IsChecked = !enabled; changing = false;
                status.Text = enabled
                    ? T("Не удалось включить автозагрузку.", "Could not enable autostart.")
                    : T("Не удалось отключить автозагрузку.", "Could not disable autostart.");
                status.IsVisible = true;
            };
            Grid.SetColumn(autostart, 1);
            autostartRow.Children.Add(autostart);
            panel.Children.Add(autostartRow);
            panel.Children.Add(status);
        }
#if !DESKTOP_SHARED
        AddUpdateControls(panel);
#endif
        var version = platform.AppVersion;
        var versionText = Text(T($"Версия {version}", $"Version {version}"), 12, true);
        versionText.HorizontalAlignment = HorizontalAlignment.Center;
        versionText.TextAlignment = TextAlignment.Center;
        panel.Children.Add(versionText);
        return panel;
    }

#if !DESKTOP_SHARED
    /// <summary>Показывает проверку версии, описание выпуска, загрузку и повтор системной установки.</summary>
    private void AddUpdateControls(StackPanel panel)
    {
        var update = Updates.AndroidUpdates.Instance;
        panel.Children.Add(Text(T("Обновления приложения", "App updates"), 17));
        var message = update.State switch
        {
            "checking" => "",
            "current" => "",
            "available" => T("Доступна новая версия.", "A new version is available."),
            "downloading" => T($"Загрузка: {update.Percent}%", $"Downloading: {update.Percent}%"),
            "verifying" => T("Проверка файла…", "Verifying download…"),
            "permission" => T("Разрешите установку из этого приложения, вернитесь и нажмите «Установить».", "Allow installs from this app, return and tap Install."),
            "installer" => T("Подтвердите обновление в установщике Android. Если отменили — можно повторить.", "Confirm the update in Android's installer. You can retry if cancelled."),
            "check-error" => T("Не удалось проверить обновление. Проверьте подключение и повторите.", "Could not verify updates. Check your connection and retry."),
            "download-error" => T("Не удалось загрузить или проверить APK. Повторите загрузку. Обновление требует той же подписи, что и установленная версия.", "Could not download or verify the APK. Retry the download. The update must use the same signing key as the installed app."),
            "cancelled" => T("Загрузка отменена.", "Download cancelled."),
            _ => T("Обновления из официального репозитория GitHub.", "Updates from the official GitHub repository.")
        };
        if (message.Length > 0) panel.Children.Add(Text(message, 13, true));
        if (update.Release is { } release)
        {
            panel.Children.Add(Text(T($"Версия {release.Version} · {release.Size / 1048576d:F1} МБ", $"Version {release.Version} · {release.Size / 1048576d:F1} MB")));
            if (release.Notes.Length > 0) panel.Children.Add(Text(release.Notes, 13, true));
            var label = update.State is "permission" or "installer" ? T("Установить", "Install") : T("Скачать и установить", "Download and install");
            var download = Button(Text(label), label, () => _ = update.DownloadOrInstallAsync());
            download.IsEnabled = !update.Busy;
            panel.Children.Add(download);
        }
        if (update.Busy)
            panel.Children.Add(Button(Text(T("Отменить", "Cancel")), T("Отменить", "Cancel"), update.Cancel));
        else
            panel.Children.Add(Button(Text(T("Проверить обновления", "Check for updates")), T("Проверить обновления", "Check for updates"), () => _ = update.CheckAsync(manual: true)));
    }

    /// <summary>Создает отдельную страницу маршрутизации приложений с возвратом в настройки.</summary>
    private Control SplitTunnelPage()
    {
        var panel = Page();
        panel.Children.Add(Header(T("Раздельное туннелирование", "Split tunneling"), new Border(),
            Button(Icon("back"), T("Назад в настройки", "Back to settings"), () => Navigate("settings"))));
        var policy = SplitTunnelPreferences.Load();
        var selected = policy.Packages.ToHashSet(StringComparer.Ordinal);
        var enabled = new ToggleSwitch { Content = T("Включить", "Enable"), IsChecked = policy.Enabled,
            OnContent = T("Включено", "On"), OffContent = T("Выключено", "Off") };
        panel.Children.Add(enabled);
        var options = new StackPanel { Spacing = 8, IsVisible = policy.Enabled };
        enabled.IsCheckedChanged += (_, _) => {
            MobilePreferences.Save("split-enabled", enabled.IsChecked == true ? "true" : "false");
            options.IsVisible = enabled.IsChecked == true;
        };
        var mode = new ComboBox { ItemsSource = new[] {
            T("Выбранные приложения без VPN", "Selected apps bypass VPN"),
            T("Только выбранные через VPN", "Only selected apps use VPN") },
            SelectedIndex = policy.IncludeOnly ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.SelectionChanged += (_, _) => MobilePreferences.Save("split-mode", mode.SelectedIndex == 1 ? "include" : "exclude");
        options.Children.Add(mode);
        options.Children.Add(Text(T("Применяется при следующем подключении. В режиме «только выбранные» отметьте хотя бы одно приложение. Показаны приложения с иконкой запуска.",
            "Applies on next connection. Select at least one app in include-only mode. Apps with a launcher icon are listed."), 12, true));
        var search = new TextBox { Watermark = T("Найти приложение", "Search apps") };
        options.Children.Add(search);
        var rows = new StackPanel { Spacing = 4 };
        foreach (var app in SplitTunnelPreferences.Applications())
        {
            var check = new global::Avalonia.Controls.CheckBox { Content = Text(app.Label + "\n" + app.Package, 13), IsChecked = selected.Contains(app.Package),
                HorizontalAlignment = HorizontalAlignment.Stretch };
            check.IsCheckedChanged += (_, _) => {
                if (check.IsChecked == true) selected.Add(app.Package); else selected.Remove(app.Package);
                MobilePreferences.Save("split-packages", string.Join('\n', selected.Order()));
            };
            search.TextChanged += (_, _) => check.IsVisible = (app.Label + " " + app.Package).Contains(search.Text ?? "", StringComparison.CurrentCultureIgnoreCase);
            rows.Children.Add(check);
        }
        options.Children.Add(rows);
        panel.Children.Add(options);
        return panel;
    }
#endif

    /// <summary>Применяет системную или явно выбранную тему к текущему приложению.</summary>
    private static void ApplyTheme() => global::Avalonia.Application.Current!.RequestedThemeVariant = MobilePreferences.Theme switch
    { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };

    /// <summary>Создает закрепленную нижнюю панель из четырех разделов.</summary>
    private Control Navigation()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        var entries = new[] { ("home", T("Главная", "Home")), ("servers", T("Серверы", "Servers")), ("profile", T("Профиль", "Profile")), ("telegram", "Telegram") };
        for (var index = 0; index < entries.Length; index++)
        {
            var (destination, label) = entries[index];
            var content = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(Icon(destination, page == destination ? ForegroundColor : Muted, 21));
            var caption = Text(label, 10, page != destination); caption.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(caption);
            var button = Button(content, label, () => { if (destination == "telegram") OpenTelegram(); else Navigate(destination); });
            button.Background = Brushes.Transparent; button.Padding = new Thickness(4, 10);
            Grid.SetColumn(button, index); grid.Children.Add(button);
        }
        return new Border { Child = grid, Background = Surface, BorderBrush = Brush(Light ? "#D9DFE5" : "#2A3036"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 0, 8, 4) };
    }

    /// <summary>Открывает модальное добавление профиля по одноразовой ссылке Telegram.</summary>
    private void OpenImport()
    {
        if (overlay is not null || !CanOpenProfile) return;
        replacementError = null;
        importFeedback = "";
        model.IsAdding = true;
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(Text(T("Добавить профиль", "Add profile"), 23));
        stack.Children.Add(Text(T("Вставьте ссылку из @BlackEagleVPNbot. Она заменит текущий профиль.", "Paste the link from @BlackEagleVPNbot. It will replace the current profile."), 14, true));
#if !DESKTOP_SHARED
        stack.Children.Add(Text(T("Перед добавлением старое VPN-соединение будет отключено.", "The existing VPN connection will be disconnected before adding the profile."), 13, true));
#endif
        var input = new TextBox { Watermark = T("Ссылка из Telegram", "Telegram link"), MinHeight = 48, TextWrapping = TextWrapping.Wrap, MaxLength = 4096 };
        input.TextChanged += (_, _) => model.Link = input.Text ?? "";
        stack.Children.Add(input);
        importMessage = Text("", 12, true); stack.Children.Add(importMessage);
        importButton = Button(Text(T("Добавить", "Add")), T("Добавить", "Add"), async () => { await ImportProfileAsync(); if (string.IsNullOrEmpty(model.Link)) input.Text = ""; QueueRender(); });
        importButton.IsEnabled = CanReplaceProfile; stack.Children.Add(importButton);
        var cancel = Button(Text(T("Отмена", "Cancel")), T("Отмена", "Cancel"), CloseImport); stack.Children.Add(cancel);
        overlay = new Grid { Background = Brush("#B8000000") };
        overlay.Children.Add(new Border { Background = Surface, CornerRadius = new CornerRadius(24), Padding = new Thickness(24), Margin = new Thickness(20),
            MaxWidth = 480, VerticalAlignment = VerticalAlignment.Center, Child = new ScrollViewer { Content = stack } });
        Grid.SetRowSpan(overlay, 2); root.Children.Add(overlay); input.Focus();
    }

    /// <summary>Заменяет профиль после остановки старого туннеля, показывая отказ вместо молчаливого возврата.</summary>
    private async Task ImportProfileAsync()
    {
        if (!CanReplaceProfile) return;
        replacingProfile = true; replacementError = null;
        importFeedback = T("Обращаемся к сервису…", "Contacting the service…");
        QueueRender();
        if (importButton is not null) importButton.IsEnabled = false;
        try
        {
#if DESKTOP_SHARED
            await model.ImportAsync();
#else
            await ProfileReplacement.ImportAsync(model);
#endif
            importFeedback = StatusMessage;
        }
        catch (Exception)
        {
            replacementError = T("Не удалось остановить старое соединение. Отключите VPN и повторите добавление — ссылка сохранена в поле.",
                "Could not stop the previous connection. Disconnect VPN and try adding again. The link remains in the field.");
        }
        finally { replacingProfile = false; QueueRender(); }
    }

    /// <summary>Закрывает окно импорта и удаляет ссылку из поля и модели.</summary>
    private void CloseImport()
    {
        if (overlay is not null) root.Children.Remove(overlay);
        overlay = null; importButton = null; importMessage = null; model.IsAdding = false;
    }

    /// <summary>Открывает только фиксированный HTTPS-адрес официального Telegram-бота.</summary>
    private void OpenTelegram()
    {
        try
        {
            platform.OpenTelegram();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            model.ReportMessage(T("Установите Telegram или браузер.", "Install Telegram or a browser."));
            QueueRender();
        }
    }

    /// <summary>Обновляет сообщение о результате новой попытки без изменения общей модели Linux.</summary>
    internal async Task ToggleConnectionAsync()
    {
        if (!model.CanConnect) return;
        var starting = model.CanSelectServer;
        if (starting) connectionFeedback.BeginAttempt();
        try
        {
            await model.ToggleConnectionAsync();
            if (starting) connectionFeedback.CompleteAttempt(platform.IsConnected);
        }
        finally { QueueRender(); }
    }

    /// <summary>Открывает страницу серверов из системного меню desktop-клиента.</summary>
    public void ShowServers() => Navigate("servers");

    /// <summary>Открывает страницу настроек для платформенной или headless-проверки.</summary>
    public void ShowSettings() => Navigate("settings");

    /// <summary>Локализует сообщения общей модели, не изменяя интерфейс и ресурсы Linux.</summary>
    private string Translate(string value) => English ? MobileText.English(value) : value;
}
