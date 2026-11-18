using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace CadreLink;

public partial class Studio {
    ObsLinks? obs;
    Window? cleanOutput;
    Image? cleanImage;
    bool obsRendered;

    void ShowObs(object sender, RoutedEventArgs args) {
        ObsNavigation.IsChecked = true;
    }

    void OpenObsWorkspace() {
        if (!obsRendered) RefreshObsLinks();
    }

    void RefreshObsLinks() {
        if (ObsQr is null || !IsInitialized) return;
        try {
            obs ??= ObsLinks.Load();
            ObsQr.Source = QrImage(obs.Phone(ObsQuality.SelectedIndex == 0, ObsMic.IsChecked == true, ObsRear.IsChecked == true));
            ObsStatus.Text = "QR et lien OBS prêts";
            obsRendered = true;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException) {
            ObsStatus.Text = "La session enregistrée est inaccessible. Clique sur Renouveler les liens.";
        }
    }

    void ObsSettingsChanged(object sender, RoutedEventArgs args) {
        if (obsRendered) RefreshObsLinks();
    }

    void RotateObsSession(object sender, RoutedEventArgs args) {
        var choice = MessageBox.Show(this,
            "Renouveler les liens ? Il faudra rescanner le nouveau QR et remplacer l’URL de la source dans OBS. Termine l’ancienne diffusion dans Safari.",
            "Nouvelle session iPhone", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;
        try {
            var next = ObsLinks.New();
            next.Save();
            obs = next;
            RefreshObsLinks();
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException) {
            ObsStatus.Text = "Impossible d’enregistrer la nouvelle session";
        }
    }

    void CopyObsLink(object sender, RoutedEventArgs args) {
        if (obs is null) return;
        try {
            Clipboard.SetText(obs.Source(ObsMic.IsChecked == true));
            ObsStatus.Text = "Lien OBS copié";
        } catch (ExternalException) { ObsStatus.Text = "Presse-papiers indisponible. Réessaie."; }
    }

    void CopyPhoneLink(object sender, RoutedEventArgs args) {
        if (obs is null) return;
        try {
            Clipboard.SetText(obs.Phone(ObsQuality.SelectedIndex == 0, ObsMic.IsChecked == true, ObsRear.IsChecked == true));
            ObsStatus.Text = "Lien Safari copié";
        } catch (ExternalException) { ObsStatus.Text = "Presse-papiers indisponible. Réessaie."; }
    }

    static BitmapImage QrImage(string link) {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        using var stream = new MemoryStream(qr.GetGraphic(8));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        return image;
    }

    void OpenCleanOutput(object sender, RoutedEventArgs args) {
        if (cleanOutput is not null) { cleanOutput.Activate(); return; }
        var image = new Image { Stretch = Stretch.Uniform };
        cleanImage = image;
        var window = new Window {
            Title = "CadreLink · Sortie propre", Width = 960, Height = 580, MinWidth = 320, MinHeight = 205,
            Background = Brushes.Black, Content = image, Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this
        };
        window.SourceInitialized += (_,_) => DarkTitle(window);
        window.Closed += (_,_) => { cleanOutput = null; cleanImage = null; };
        cleanOutput = window;
        UpdateCleanOutput();
        window.Show();
    }

    void UpdateCleanOutput() {
        if (cleanImage is not null) cleanImage.Source = hidden ? null : Video.Source;
    }
}