using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CadreLink;

public partial class Studio {
    static readonly string ProfilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Focale", "profile.json");

    sealed record Profile(string Lens, double Zoom, int Mode, int Rotation, bool Natural, int Exposure,
        bool ExposureLocked, bool WhiteBalanceLocked, bool Mirror, bool Guides, bool Topmost, bool AutoFrame = false,
        FramingMode Framing = FramingMode.Off);
    sealed record NamedProfile(string Name, Profile Settings);
    readonly NamedProfile?[] profiles = new NamedProfile?[3];
    static string ProfilesPath => Path.Combine(Path.GetDirectoryName(ProfilePath)!, "profiles.json");
    Profile? pendingProfile;
    int rotation;
    bool hidden, expanded;
    double receivedFps, receivedMbps;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    void DarkTitle(Window window) {
        var enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
    }

    void SetStatus(string text, string color = "Muted") {
        Status.Text = text;
        StatusDot.Fill = (Brush)FindResource(color);
    }

    void HideOutput(object sender, RoutedEventArgs args) {
        if (server is null) return;
        hidden = !hidden;
        server.Hidden = hidden;
        HiddenBanner.Visibility = hidden && Video.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        HideButton.Content = hidden ? "Rétablir" : "Masquer";
        Message.Text = hidden ? "Sortie Windows masquée" : "Sortie Windows rétablie";
        SyncMini();
        UpdateCleanOutput();
    }

    async void Snapshot(object sender, RoutedEventArgs args) {
        if (Video.Source is null || server is null) return;
        try {
            var frame = await server.Capture();
            var picker = new SaveFileDialog {
                Title = "Enregistrer la photo", Filter = "Image PNG (*.png)|*.png",
                FileName = $"CadreLink-{DateTime.Now:yyyy-MM-dd-HHmmss}.png", AddExtension = true
            };
            if (picker.ShowDialog(this) != true) return;
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32,
                null, frame.Pixels, frame.Width * 4);
            bitmap.Freeze();
            await Task.Run(() => {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string temporary = picker.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    using (var file = File.Create(temporary)) encoder.Save(file);
                    File.Move(temporary, picker.FileName, overwrite: true);
                } finally {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            });
            Message.Text = $"Photo enregistrée · {frame.Width} × {frame.Height}";
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException
            or OperationCanceledException or InvalidOperationException) {
            Message.Text = "Photo indisponible · " + error.Message;
        }
    }

    void Expand(object sender, RoutedEventArgs args) {
        expanded = !expanded;
        Rail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        Navigation.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        NavColumn.Width = new GridLength(expanded ? 0 : 176);
        RailColumn.Width = expanded ? new GridLength(0) : new GridLength(300);
        ExpandButton.Content = expanded ? "Afficher les réglages" : "Agrandir";
    }

    void LayoutChanged(object sender, RoutedEventArgs args) {
        Video.RenderTransform = new ScaleTransform(Mirror.IsChecked == true ? -1 : 1, 1);
        Guides.Visibility = GridLines.IsChecked == true && Video.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        Topmost = OnTop.IsChecked == true;
        UpdateGlass();
        SyncMini();
    }

    void Rotate(object sender, RoutedEventArgs args) {
        rotation = (rotation + 90) % 360;
        ApplyRotation();
    }

    void ApplyRotation() {
        if (server is not null) server.Rotation = rotation;
        RotationButton.Content = rotation == 0 ? "Pivoter de 90°" : $"Pivoter de 90° · {rotation}°";
    }

    void OnKey(object sender, KeyEventArgs args) {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.F11) Expand(this, args);
        else if (key == Key.P && Keyboard.Modifiers == ModifierKeys.Control) HideOutput(this, args);
        else if (key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) Snapshot(this, args);
        else return;
        args.Handled = true;
    }

    void Reconnect(object sender, RoutedEventArgs args) {
        if (server?.HasPeer == true) {
            SetStatus("Reconnexion du téléphone…", "Waiting");
            server.Reconnect();
        } else if (address is not null) {
            Start(address);
        } else {
            RefreshAddresses();
            if (addresses.Count > 0) Start(addresses[0]);
        }
    }

    void CopyDiagnostic(object sender, RoutedEventArgs args) {
        try {
            Clipboard.SetText($"CadreLink 2.0\n{Status.Text}\n{Format.Text}\n{Transport.Text}\n{NetworkRate.Text}\n{LatencyText.Text}\nCadrage : {TrackingState.Text}\nObjectif : {(Lenses.SelectedItem as Lens)?.Name ?? "—"}\nRendu naturel : {Natural.IsChecked == true}\nSortie masquée : {hidden}");
            Message.Text = "Diagnostic copié";
        } catch (ExternalException) { Message.Text = "Presse-papiers indisponible. Réessaie."; }
    }

    void LoadProfiles() {
        try {
            if (File.Exists(ProfilesPath)) {
                var saved = JsonSerializer.Deserialize<NamedProfile?[]>(File.ReadAllText(ProfilesPath));
                if (saved is not null) Array.Copy(saved, profiles, Math.Min(saved.Length, profiles.Length));
            } else if (File.Exists(ProfilePath) && JsonSerializer.Deserialize<Profile>(File.ReadAllText(ProfilePath)) is { } legacy) {
                profiles[0] = new NamedProfile("Profil précédent", legacy);
            }
        } catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) {
            Message.Text = "Impossible de lire les profils";
        }
        RefreshProfiles(0);
    }

    void RefreshProfiles(int selected) {
        ProfileSlots.ItemsSource = profiles.Select((profile, index) =>
            $"{index + 1} · {profile?.Name ?? "Emplacement vide"}").ToArray();
        ProfileSlots.SelectedIndex = selected;
        UpdateProfileButtons();
    }

    void ProfileSlotChanged(object sender, SelectionChangedEventArgs args) {
        if (ProfileName is null || ProfileSlots.SelectedIndex < 0) return;
        ProfileName.Text = profiles[ProfileSlots.SelectedIndex]?.Name ?? "";
        UpdateProfileButtons();
    }

    void UpdateProfileButtons() {
        if (SaveButton is null || ProfileSlots.SelectedIndex < 0) return;
        SaveButton.IsEnabled = Lenses.SelectedItem is Lens;
        RestoreButton.IsEnabled = SaveButton.IsEnabled && profiles[ProfileSlots.SelectedIndex] is not null;
    }

    void SaveProfile(object sender, RoutedEventArgs args) {
        int slot = ProfileSlots.SelectedIndex;
        if (slot < 0 || Lenses.SelectedItem is not Lens lens) return;
        var profile = new Profile(lens.Id, Zoom.Value, Rate.SelectedIndex, rotation,
            Natural.IsChecked == true, (int)Exposure.Value, AeLock.IsChecked == true, AwbLock.IsChecked == true,
            Mirror.IsChecked == true, GridLines.IsChecked == true, OnTop.IsChecked == true, SelectedFraming != FramingMode.Off, SelectedFraming);
        string name = string.IsNullOrWhiteSpace(ProfileName.Text) ? $"Profil {slot + 1}" : ProfileName.Text.Trim();
        try {
            var saved = (NamedProfile?[])profiles.Clone();
            saved[slot] = new NamedProfile(name, profile);
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
            File.WriteAllText(ProfilesPath + ".tmp", JsonSerializer.Serialize(saved));
            File.Move(ProfilesPath + ".tmp", ProfilesPath, true);
            profiles[slot] = saved[slot];
            RefreshProfiles(slot);
            ProfileName.Text = name;
            Message.Text = $"{name} mémorisé";
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            Message.Text = "Impossible de mémoriser le profil";
        }
    }

    async void RestoreProfile(object sender, RoutedEventArgs args) {
        if (ProfileSlots.SelectedIndex >= 0) await ApplyProfileSlot(ProfileSlots.SelectedIndex);
    }

    async Task ApplyProfileSlot(int slot) {
        if (profiles[slot] is not { } named) { Message.Text = $"Profil {slot + 1} encore vide"; return; }
        var profile = named.Settings;
        if (profile is null || profile.Mode is < 0 or > 2 || profile.Rotation is not (0 or 90 or 180 or 270)) {
            Message.Text = "Ce profil ne peut pas être lu.";
            return;
        }
        var lens = Lenses.Items.Cast<Lens>().FirstOrDefault(item => item.Id == profile.Lens);
        if (lens is null) { Message.Text = "L’objectif du profil est indisponible sur ce téléphone."; return; }
        debounce.Stop();
        updating = true;
        ProfileSlots.SelectedIndex = slot;
        Rate.SelectedIndex = profile.Mode;
        Natural.IsChecked = profile.Natural;
        Mirror.IsChecked = profile.Mirror;
        GridLines.IsChecked = profile.Guides;
        OnTop.IsChecked = profile.Topmost;
        BodyMode.SelectedIndex = profile.Framing != FramingMode.Off && Enum.IsDefined(profile.Framing)
            ? (int)profile.Framing : profile.AutoFrame ? (int)FramingMode.Body : 0;
        ApplyFraming();
        rotation = profile.Rotation;
        ApplyRotation();
        LayoutChanged(this, new RoutedEventArgs());
        updating = false;
        pendingProfile = profile;
        await Command(new {
            lens = lens.Id, zoom = Math.Clamp(profile.Zoom, lens.Min, lens.Max),
            fps = profile.Mode == 0 ? 60 : 30, width = profile.Mode == 2 ? 3840 : 1920,
            natural = profile.Natural, exposure = Math.Clamp(profile.Exposure, lens.EvMin, lens.EvMax),
            aeLock = lens.AeLock && profile.ExposureLocked, awbLock = lens.AwbLock && profile.WhiteBalanceLocked,
            report = true
        });
    }
}
