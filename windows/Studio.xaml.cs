using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Net;
using System.Text.Json;
using System.Runtime.InteropServices;
using QRCoder;

namespace CadreLink;
public partial class Studio : Window {
    Receiver? server;
    IPAddress? address;
    List<IPAddress> addresses = [];
    bool updating;
    long lastPictureAt;
    (Receiver Receiver, PreviewFrame Frame)? pendingPicture;
    readonly object pictureLock = new();
    bool pictureQueued, closing;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(65) };
    readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly WriteableBitmap preview = new(640,360,96,96,PixelFormats.Bgra32,null);
    record Lens(string Id, string Name, double Min, double Max, int EvMin, int EvMax, double EvStep, bool AeLock, bool AwbLock);

    public Studio() {
        updating = true;
        InitializeComponent();
        LoadCallTools();
        updating = false;
        ApplyFraming();
        SourceInitialized += (_,_) => DarkTitle(this);
        PreviewKeyDown += OnKey;
        debounce.Tick += async (_,_) => {
            debounce.Stop();
            await Command(new { zoom = Zoom.Value, exposure = (int)Exposure.Value });
        };
        watchdog.Tick += (_,_) => {
            if (lastPictureAt != 0 && Environment.TickCount64 - lastPictureAt > 1100) {
                SetStatus("Signal vidéo en attente…", "Waiting");
                NetworkRate.Text = "Image en attente";
            }
        };
        watchdog.Start();
        RefreshAddresses();
        Start(addresses.FirstOrDefault() ?? IPAddress.Loopback);
        Closed += (_,_) => {
            watchdog.Stop(); debounce.Stop();
            lock (pictureLock) {
                closing = true;
                pendingPicture?.Frame.Dispose();
                pendingPicture = null;
            }
            server?.Dispose();
        };
    }

    void RefreshAddresses() => addresses = Receiver.Addresses();

    void Start(IPAddress ip) {
        server?.Dispose();
        server = null;
        address = ip;
        Reset();
        try {
            var receiver = new Receiver(ip, allInterfaces: true) { Hidden = hidden, Rotation = rotation, Framing = SelectedFraming };
            server = receiver;
            void UI(Action update) => Dispatcher.BeginInvoke(() => { if (server == receiver) update(); });
            receiver.Status += message => UI(() => {
                SetStatus(message, receiver.HasPeer ? "Waiting" : "Muted");
                if (receiver.HasPeer) Transport.Text = "Liaison · " + receiver.Connection;
            });
            receiver.Tracking += state => UI(() => TrackingState.Text = state);
            receiver.Disconnected += reason => UI(() => {
                if (receiver.HasPeer) return;
                Reset();
                Message.Text = reason;
            });
            receiver.Latency += milliseconds => UI(() => LatencyText.Text = $"Aller-retour · {milliseconds} ms");
            receiver.Metadata += data => UI(() => Update(data));
            receiver.Signal += (fps, mbps) => UI(() => {
                receivedFps = fps; receivedMbps = mbps;
                NetworkRate.Text = $"{receivedFps:0.#} i/s   ·   {receivedMbps:0.0} Mbit/s";
            });
            receiver.Picture += picture => QueuePicture(receiver, picture);
            _ = receiver.Listen();
            SetStatus("En attente du téléphone");
        } catch (Exception error) {
            SetStatus(error is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                ? "Port vidéo déjà utilisé sur ce PC"
                : "Connexion réseau indisponible", "Error");
            Message.Text = error.Message;
        }
    }

    void QueuePicture(Receiver sender, PreviewFrame frame) {
        lock (pictureLock) {
            if (closing) { frame.Dispose(); return; }
            pendingPicture?.Frame.Dispose();
            pendingPicture = (sender, frame);
            if (pictureQueued) return;
            pictureQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderPicture));
        }
    }

    void RenderPicture() {
        (Receiver Receiver, PreviewFrame Frame)? next;
        lock (pictureLock) {
            next = pendingPicture;
            pendingPicture = null;
            pictureQueued = false;
        }
        if (next is not { } frame) return;
        using (frame.Frame) {
            if (server != frame.Receiver || !frame.Receiver.HasPeer) return;
            lastPictureAt = Environment.TickCount64;
            preview.WritePixels(new Int32Rect(0, 0, PreviewFrame.Width, PreviewFrame.Height),
                frame.Frame.Pixels, PreviewFrame.Stride, 0);
            if (Video.Source is null) {
                Video.Source = preview;
                OutputActions.IsEnabled = true;
                Empty.Visibility = Visibility.Collapsed;
                HiddenBanner.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
                LayoutChanged(this, new RoutedEventArgs());
                UpdateCleanOutput();
            }
            if (!Equals(StatusDot.Fill, FindResource("Live")))
                SetStatus("Téléphone connecté · " + frame.Receiver.Connection, "Live");
        }
    }

    void Reset() {
        updating = true;
        debounce.Stop();
        lastPictureAt = 0;
        pendingProfile = null;
        Controls.IsEnabled = ExposureControls.IsEnabled = OutputActions.IsEnabled = false;
        SaveButton.IsEnabled = RestoreButton.IsEnabled = false;
        Video.Source = null;
        UpdateCleanOutput();
        UpdateGlass();
        SyncMini();
        Empty.Visibility = Visibility.Visible;
        Guides.Visibility = HiddenBanner.Visibility = Visibility.Collapsed;
        Lenses.ItemsSource = null;
        Presets.Children.Clear();
        Rate.SelectedIndex = 0;
        AeLock.IsChecked = AwbLock.IsChecked = false;
        Format.Text = "En attente du signal";
        NetworkRate.Text = "Aucun flux";
        TrackingState.Text = SelectedFraming != FramingMode.Off ? "En attente du téléphone" : "Désactivé";
        LatencyText.Text = "Aller-retour · —";
        Transport.Text = "Aucune liaison";
        Battery.Text = "Téléphone hors ligne";
        updating = false;
        SetStatus("En attente du téléphone");
    }

    async Task Command(object value) {
        try { if (server is not null) await server.Send(value); }
        catch (Exception) { Message.Text = "Commande interrompue · vérifie la liaison"; }
    }

    static int GetInt(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) ? value.GetInt32() : 0;
    static bool GetBool(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.GetBoolean();

    async void Update(JsonElement data) {
        switch (data.GetProperty("type").GetString()) {
            case "lenses":
                Lenses.ItemsSource = data.GetProperty("items").EnumerateArray()
                    .Select(lens => new Lens(lens.GetProperty("id").GetString()!, lens.GetProperty("name").GetString()!,
                        lens.GetProperty("min").GetDouble(), lens.GetProperty("max").GetDouble(),
                        GetInt(lens,"evMin"),GetInt(lens,"evMax"),lens.TryGetProperty("evStep",out var ev) ? ev.GetDouble() : 0,
                        GetBool(lens,"aeLock"),GetBool(lens,"awbLock"))).ToList();
                break;
            case "format":
            case "settings":
                bool firstFormat = !Controls.IsEnabled;
                updating = true;
                string? id = data.GetProperty("lens").GetString();
                var lens = Lenses.Items.Cast<Lens>().FirstOrDefault(item => item.Id == id);
                bool switched = !Equals(Lenses.SelectedItem,lens);
                Lenses.SelectedItem = lens;
                Controls.IsEnabled = lens is not null;
                UpdateProfileButtons();
                ConfigureZoom(data.GetProperty("zoom").GetDouble());
                if (switched || pendingProfile is not null) ConfigureExposure(lens);
                updating = false;
                if (data.TryGetProperty("width",out var width))
                    Format.Text = $"{width} × {data.GetProperty("height")} · cible {data.GetProperty("fps")} i/s";
                if (pendingProfile is { } profile) {
                    Message.Text = id == profile.Lens ? "Profil rappelé" : "Objectif du profil refusé par Android";
                    pendingProfile = null;
                } else if (firstFormat) {
                    await Command(new { natural = Natural.IsChecked == true });
                }
                break;
            case "battery":
                int charge = GetInt(data, "percent");
                Battery.Text = GetBool(data,"charging") ? $"En charge · {charge} %" : $"Batterie · {charge} %";
                break;
            case "warning":
                Message.Text = data.GetProperty("text").GetString();
                break;
        }
    }

    void ConfigureZoom(double value) {
        if (Lenses.SelectedItem is not Lens lens) return;
        Zoom.Minimum = lens.Min;
        Zoom.Maximum = lens.Max;
        Zoom.Value = Math.Clamp(value,lens.Min,lens.Max);
        Zoom.IsEnabled = lens.Max > lens.Min;
        Presets.Children.Clear();
        foreach(var ratio in new[]{.6,1,2,3,5,10}.Where(z => z >= lens.Min - .001 && z <= lens.Max + .001)) {
            var button = new Button { Content = $"{ratio:0.#}×", Padding = new Thickness(9,3,9,3), ToolTip = "Rapport de zoom" };
            button.Click += (_,_) => Zoom.Value = ratio;
            Presets.Children.Add(button);
        }
        ZoomText.Text = $"{Zoom.Value:0.00}×";
    }

    void ConfigureExposure(Lens? lens) {
        ExposureControls.IsEnabled = lens is not null;
        Exposure.Minimum = lens?.EvMin ?? 0;
        Exposure.Maximum = lens?.EvMax ?? 0;
        Exposure.IsEnabled = lens is not null && lens.EvMin < lens.EvMax;
        AeLock.IsEnabled = lens?.AeLock == true;
        AwbLock.IsEnabled = lens?.AwbLock == true;
        bool restoring = pendingProfile is { } profile && profile.Lens == lens?.Id;
        Exposure.Value = restoring ? pendingProfile!.Exposure : 0;
        AeLock.IsChecked = restoring && AeLock.IsEnabled && pendingProfile!.ExposureLocked;
        AwbLock.IsChecked = restoring && AwbLock.IsEnabled && pendingProfile!.WhiteBalanceLocked;
        UpdateExposureText();
    }

    async void LensChanged(object sender, SelectionChangedEventArgs args) {
        if (updating || Lenses.SelectedItem is not Lens lens) return;
        debounce.Stop();
        updating = true;
        ConfigureZoom(1);
        ConfigureExposure(lens);
        updating = false;
        await Command(new { lens = lens.Id });
    }

    void QueueAdjustments() {
        if (updating || !Controls.IsEnabled) return;
        debounce.Stop(); debounce.Start();
    }

    void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> args) {
        if (ZoomText is null) return;
        ZoomText.Text = $"{args.NewValue:0.00}×";
        QueueAdjustments();
    }

    void UpdateExposureText() {
        if (ExposureText is null || Exposure is null) return;
        var step = (Lenses.SelectedItem as Lens)?.EvStep ?? 0;
        ExposureText.Text = $"{Exposure.Value * step:+0.##;-0.##;0} IL";
    }

    void ExposureChanged(object sender, RoutedPropertyChangedEventArgs<double> args) {
        UpdateExposureText();
        QueueAdjustments();
    }

    async void ImageChanged(object sender, RoutedEventArgs args) {
        if (updating || !Controls.IsEnabled) return;
        await Command(new { natural = Natural.IsChecked == true, aeLock = AeLock.IsChecked == true, awbLock = AwbLock.IsChecked == true });
    }

    async void RateChanged(object sender, SelectionChangedEventArgs args) {
        if (!updating && server is not null && Controls.IsEnabled)
            await Command(new { fps = Rate.SelectedIndex == 0 ? 60 : 30, width = Rate.SelectedIndex == 2 ? 3840 : 1920 });
    }

    void PairClick(object sender, RoutedEventArgs args) {
        RefreshAddresses();
        if (server is null) Start(addresses.FirstOrDefault() ?? IPAddress.Loopback);
        var pane = new StackPanel { Margin = new Thickness(24) };
        var dialog = new Window {
            Owner = this, Title = "Associer un téléphone", Content = pane, Width = 460, Height = 650,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Surface"), Foreground = (Brush)FindResource("Ink"), Resources = Resources
        };
        dialog.SourceInitialized += (_,_) => DarkTitle(dialog);
        pane.Children.Add(new TextBlock { Text = "Associer un téléphone", FontSize = 22, FontWeight = FontWeights.SemiBold });
        var mode = new ComboBox { ItemsSource = new[] {"Wi-Fi / réseau local", "USB · partage de connexion"}, SelectedIndex = 0, Margin = new Thickness(0,10,0,8) };
        pane.Children.Add(mode);
        var instructions = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 50, Margin = new Thickness(0,0,0,8) };
        pane.Children.Add(instructions);
        var adapters = new ComboBox { DisplayMemberPath = "Display", Margin = new Thickness(0,0,0,8) };
        pane.Children.Add(adapters);
        var refresh = new Button { Content = "Actualiser les interfaces", HorizontalAlignment = HorizontalAlignment.Left };
        pane.Children.Add(refresh);
        var codeImage = new Image { Width = 236, Height = 236, Margin = new Thickness(0,6,0,8) };
        RenderOptions.SetBitmapScalingMode(codeImage, BitmapScalingMode.NearestNeighbor);
        pane.Children.Add(codeImage);
        var toggle = new Button { Margin = new Thickness(0,6,0,8) };
        pane.Children.Add(toggle);
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption") };
        pane.Children.Add(detail);
        PhoneInstaller? download = null;
        bool installing = false, refreshing = false;

        void Draw() {
            download?.Dispose();
            download = null;
            codeImage.Source = null;
            bool usb = mode.SelectedIndex == 1;
            instructions.Text = usb
                ? "Branche un câble de données. Sur le Samsung, ouvre Paramètres → Connexions → Point d’accès mobile et modem → Modem USB."
                : "Connecte le PC et le téléphone au même réseau. Dans CadreLink pour Android, ouvre le scanner de QR code.";
            toggle.Content = installing ? "Revenir au code d’association" : "Installer ou mettre à jour Android";
            if (server is null || adapters.SelectedItem is not Receiver.Adapter adapter) {
                detail.Text = usb ? "Active le partage USB et actualise les interfaces." : "Connecte le PC au réseau et actualise les interfaces.";
                return;
            }
            string link = server.LinkFor(adapter.Address, usb);
            if (installing) {
                try {
                    download = new PhoneInstaller(adapter.Address);
                    link = download.Url;
                } catch (Exception error) {
                    detail.Text = error.Message;
                    return;
                }
            }
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            using var stream = new MemoryStream(qr.GetGraphic(6));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            codeImage.Source = image;
            detail.Text = installing
                ? "Scanne avec l’appareil photo Samsung, puis ouvre l’APK téléchargé. Le lien local expire après cinq minutes."
                : usb ? "Scanne avec CadreLink sur Android. Si l’interface USB est mal identifiée, sélectionne celle apparue après le branchement."
                      : "Scanne avec CadreLink sur Android. Le téléphone mémorise cette connexion.";
        }

        void UpdateAdapters() {
            refreshing = true;
            var all = Receiver.Adapters();
            bool usb = mode.SelectedIndex == 1;
            var choices = all.OrderBy(adapter => adapter.IsUsb != usb).ToList();
            adapters.ItemsSource = choices;
            adapters.SelectedItem = usb
                ? choices.FirstOrDefault(adapter => adapter.IsUsb)
                : choices.FirstOrDefault(adapter => !adapter.IsUsb) ?? choices.FirstOrDefault();
            refreshing = false;
            Draw();
        }
        refresh.Click += (_,_) => UpdateAdapters();
        mode.SelectionChanged += (_,_) => UpdateAdapters();
        adapters.SelectionChanged += (_,_) => { if (!refreshing) Draw(); };
        toggle.Click += (_,_) => { installing = !installing; Draw(); };
        dialog.Closed += (_,_) => download?.Dispose();
        UpdateAdapters();
        dialog.ShowDialog();
    }

}
internal static class Program {
    [STAThread] static int Main(string[] args){
        if(args.FirstOrDefault() is "--install-camera" or "--remove-camera"){
            int hr=Native.CameraSetup(args[0]=="--remove-camera");return hr<0?hr:0;
        }
        using var mutex=new Mutex(true,"Local\\CadreLink-Studio-13dfe35a",out bool first);
        if(!first){MessageBox.Show("Le studio CadreLink est déjà ouvert.","CadreLink");return 0;}
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new Studio());
    }
}
