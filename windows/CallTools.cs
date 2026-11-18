using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace CadreLink;

public partial class Studio {
    static string StudioSettingsPath => Path.Combine(Path.GetDirectoryName(ProfilePath)!, "studio-settings.json");
    sealed record StudioSettings(bool GlobalKeys, bool AutoFrame, FramingMode Framing = FramingMode.Off);
    FramingMode SelectedFraming => BodyMode?.SelectedIndex is >= 1 and <= 3 ? (FramingMode)BodyMode.SelectedIndex : FramingMode.Off;
    Window? mini;
    Image? miniVideo;
    TextBlock? miniStatus;
    Button? miniMask;
    HwndSource? hotkeys;
    const uint ControlAlt = 0x0001 | 0x0002 | 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);

    void LoadCallTools() {
        LoadProfiles();
        try {
            if (File.Exists(StudioSettingsPath) &&
                JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(StudioSettingsPath)) is { } settings) {
                GlobalKeys.IsChecked = settings.GlobalKeys;
                BodyMode.SelectedIndex = settings.Framing != FramingMode.Off && Enum.IsDefined(settings.Framing)
                    ? (int)settings.Framing : settings.AutoFrame ? (int)FramingMode.Body : 0;
            }
        } catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) {
            Message.Text = "Préférences locales indisponibles";
        }
        SourceInitialized += (_, _) => {
            hotkeys = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hotkeys?.AddHook(HotkeyMessage);
            ApplyGlobalKeys();
        };
        Closed += (_, _) => { ReleaseKeys(); mini?.Close(); };
    }

    void SaveCallSettings() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(StudioSettingsPath)!);
            File.WriteAllText(StudioSettingsPath + ".tmp", JsonSerializer.Serialize(
                new StudioSettings(GlobalKeys.IsChecked == true, SelectedFraming != FramingMode.Off, SelectedFraming)));
            File.Move(StudioSettingsPath + ".tmp", StudioSettingsPath, true);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            Message.Text = "Préférences locales non enregistrées";
        }
    }

    void Navigate(object sender, RoutedEventArgs args) {
        if (Inspector is null || sender is not RadioButton { Tag: string tag }) return;
        int index = int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        bool useObs = index == 4;
        ObsWorkspace.Visibility = useObs ? Visibility.Visible : Visibility.Collapsed;
        CameraStage.Visibility = Rail.Visibility = useObs ? Visibility.Collapsed : Visibility.Visible;
        if (useObs) OpenObsWorkspace();
        else Inspector.SelectedIndex = index;
        if (SystemParameters.ClientAreaAnimation)
            (useObs ? (UIElement)ObsWorkspace : Inspector).BeginAnimation(OpacityProperty,
                new DoubleAnimation(.74, 1, TimeSpan.FromMilliseconds(180)));
    }

    void MinimizeWindow(object sender, RoutedEventArgs args) => SystemCommands.MinimizeWindow(this);
    void MaximizeWindow(object sender, RoutedEventArgs args) {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    void CloseWindow(object sender, RoutedEventArgs args) => Close();

    void TrackingChanged(object sender, RoutedEventArgs args) {
        if (updating || GlassFollow is null || TrackingState is null) return;
        ApplyFraming();
        SaveCallSettings();
    }

    void ToggleTracking(object sender, RoutedEventArgs args) {
        BodyMode.SelectedIndex = SelectedFraming == FramingMode.Off ? (int)FramingMode.Body : 0;
    }

    void ApplyFraming() {
        var mode = SelectedFraming;
        if (server is not null) server.Framing = mode;
        GlassFollow.Content = mode switch {
            FramingMode.Body => "Corps",
            FramingMode.UpperBody => "Buste",
            FramingMode.Face => "Visage",
            _ => "Suivre"
        };
        TrackingState.Text = mode == FramingMode.Off ? "Désactivé" : "En attente du sujet";
    }

    async void QuickProfile(object sender, RoutedEventArgs args) {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int slot))
            await ApplyProfileSlot(slot);
    }

    void UpdateGlass() {
        if (Video.Source is null) {
            GlassToolbar.Visibility = Visibility.Collapsed;
            return;
        }
        GlassToolbar.Visibility = Visibility.Visible;
        bool mirror = Mirror.IsChecked == true;
        var brush = new ImageBrush(preview) {
            Stretch = Stretch.Fill, Viewbox = new Rect(156.0 / 640, 304.0 / 360, 328.0 / 640, 42.0 / 360),
            RelativeTransform = mirror ? new ScaleTransform(-1, 1, .5, .5) : Transform.Identity
        };
        if (RenderCapability.IsPixelShaderVersionSupported(2, 0) || RenderCapability.IsPixelShaderVersionSupportedInSoftware(2, 0)) {
            GlassSurface.Background = brush;
            GlassSurface.Effect = new Glass();
        }
    }

    void ToggleMini(object sender, RoutedEventArgs args) {
        if (mini is not null) { mini.Close(); return; }
        var previewFrame = new Grid { Background = (Brush)FindResource("Base") };
        miniVideo = new Image { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(.5,.5) };
        miniStatus = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        previewFrame.Children.Add(miniVideo);
        previewFrame.Children.Add(miniStatus);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8,6,2,1) };
        miniMask = new Button { MinHeight = 26, Padding = new Thickness(8,2,8,2) };
        miniMask.Click += HideOutput;
        actions.Children.Add(miniMask);
        for (int index = 0; index < 3; ++index) {
            int slot = index;
            var button = new Button { Content = (index+1).ToString(), ToolTip = $"Rappeler le profil {index+1}", MinHeight = 26, Padding = new Thickness(8,2,8,2) };
            button.Click += async (_, _) => await ApplyProfileSlot(slot);
            actions.Children.Add(button);
        }
        var returnButton = new Button { Content = "Studio", MinHeight = 26, Padding = new Thickness(8,2,8,2) };
        returnButton.Click += (_, _) => {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        };
        actions.Children.Add(returnButton);
        var root = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(previewFrame);
        var window = new Window {
            Title = "CadreLink · Mini", Width = 360, Height = 265, MinWidth = 344, MinHeight = 208,
            Topmost = true, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow,
            Background = (Brush)FindResource("Surface"), Foreground = (Brush)FindResource("Ink"), Resources = Resources,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = root
        };
        mini = window;
        window.Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - window.Width - 16);
        window.Top = SystemParameters.WorkArea.Top + 40;
        window.SourceInitialized += (_, _) => DarkTitle(window);
        window.Closed += (_, _) => { mini = null; miniVideo = null; miniStatus = null; miniMask = null; };
        window.PreviewKeyDown += OnKey;
        SyncMini();
        window.Show();
    }

    void SyncMini() {
        if (miniVideo is null || miniStatus is null || miniMask is null) return;
        miniVideo.Source = Video.Source;
        miniVideo.RenderTransform = new ScaleTransform(Mirror.IsChecked == true ? -1 : 1, 1);
        miniStatus.Text = "Caméra en attente";
        miniStatus.Visibility = Video.Source is null ? Visibility.Visible : Visibility.Collapsed;
        miniMask.Content = hidden ? "Rétablir" : "Masquer";
        miniMask.ToolTip = hidden ? "Sortie masquée · aperçu local" : "Masquer la sortie Windows";
        miniMask.Foreground = (Brush)FindResource(hidden ? "Waiting" : "Ink");
    }

    void GlobalKeysChanged(object sender, RoutedEventArgs args) {
        ApplyGlobalKeys();
        SaveCallSettings();
    }

    void ApplyGlobalKeys() {
        if (hotkeys is null) return;
        ReleaseKeys();
        if (GlobalKeys.IsChecked != true) return;
        int[] keys = [0x31, 0x32, 0x33, 0x78, 0x79];
        bool all = true;
        for (int id = 0; id < keys.Length; ++id)
            all &= RegisterHotKey(hotkeys.Handle, id + 1, ControlAlt, (uint)keys[id]);
        if (!all) Message.Text = "Certains raccourcis sont déjà utilisés dans Windows";
    }

    void ReleaseKeys() {
        if (hotkeys is null || hotkeys.IsDisposed) return;
        for (int id = 1; id <= 5; ++id) UnregisterHotKey(hotkeys.Handle, id);
    }

    IntPtr HotkeyMessage(IntPtr window, int message, IntPtr parameter, IntPtr data, ref bool handled) {
        if (message != 0x0312) return IntPtr.Zero;
        handled = true;
        int id = parameter.ToInt32();
        if (id is >= 1 and <= 3) _ = ApplyProfileSlot(id - 1);
        if (id == 4) HideOutput(this, new RoutedEventArgs());
        if (id == 5) ToggleMini(this, new RoutedEventArgs());
        return IntPtr.Zero;
    }
}