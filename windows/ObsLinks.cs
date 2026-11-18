using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CadreLink;

internal sealed record ObsLinks(string Id, string Password) {
    static string PathForSession => Path.Combine(AppData.Folder, "obs-session.dat");

    public static ObsLinks New() => new(
        "cl" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));

    public string Phone(bool hd, bool microphone, bool rear = true) =>
        $"https://vdo.ninja/?push={Id}&webcam&quality={(hd ? 0 : 1)}&maxframerate=30" +
        (rear ? "&videodevice=back" : "") + (microphone ? "" : "&audiodevice=0") + $"#p={Password}";

    public string Source(bool microphone) =>
        $"https://vdo.ninja/?view={Id}&cleanoutput" + (microphone ? "" : "&noaudio") + $"#p={Password}";

    public static ObsLinks Load() {
        if (File.Exists(PathForSession)) {
            byte[] payload = ProtectedData.Unprotect(File.ReadAllBytes(PathForSession), null, DataProtectionScope.CurrentUser);
            try {
                if (JsonSerializer.Deserialize<ObsLinks>(payload) is { } value &&
                    value.Id is { Length: 34 } && value.Id.StartsWith("cl", StringComparison.Ordinal) &&
                    value.Id[2..].All(Uri.IsHexDigit) && value.Password is { Length: 48 } &&
                    value.Password.All(Uri.IsHexDigit))
                    return value;
            } finally { CryptographicOperations.ZeroMemory(payload); }
        }
        var created = New();
        created.Save();
        return created;
    }

    public void Save() {
        Directory.CreateDirectory(AppData.Folder);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(this);
        try {
            var temporary = PathForSession + ".tmp";
            File.WriteAllBytes(temporary, ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, PathForSession, overwrite: true);
        } finally { CryptographicOperations.ZeroMemory(payload); }
    }
}