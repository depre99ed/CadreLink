using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Storage.Streams;

namespace CadreLink;

internal enum FramingMode { Off, Body, UpperBody, Face }

internal sealed class AutoFraming {
    internal const int Width = 320, Height = 180;
    public record struct Crop(float X, float Y, float Zoom);
    static readonly Crop FullFrame = new(.5f, .5f, 1f);
    readonly byte[] luminance = new byte[Width * Height];
    readonly byte[] rgb = new byte[BodyPose.Size * BodyPose.Size * BodyPose.Channels];
    readonly BodyPose pose = new();
    Task<Crop?>? detection;
    FaceDetector? detector;
    Crop current = FullFrame, target = FullFrame;
    long nextDetection, lastFrame, lastSubject;
    FramingMode mode;
    bool ignoreResult;
    public string State { get; private set; } = "Désactivé";

    public void Reset() {
        ignoreResult = detection is not null;
        target = current = FullFrame;
        lastSubject = nextDetection = lastFrame = 0;
    }

    public Crop BeforeFrame(FramingMode requested, long now) {
        if (mode != requested) {
            mode = requested;
            Reset();
        }
        if (detection is { IsCompleted: true }) {
            if (detection.IsFaulted) {
                _ = detection.Exception;
                if (!ignoreResult) {
                    State = "Suivi indisponible sur ce PC";
                    target = FullFrame;
                    lastSubject = 0;
                    nextDetection = now + 10000;
                }
            } else if (!ignoreResult && detection.Result is { } subject) {
                target = Stabilize(target, subject);
                lastSubject = now;
            }
            detection = null;
            ignoreResult = false;
        }
        if (mode == FramingMode.Off) {
            State = "Désactivé";
            return FullFrame;
        }
        if (lastSubject == 0 || now - lastSubject > 1100) {
            target = FullFrame;
            if (nextDetection <= now + 700)
                State = mode == FramingMode.Face ? "Visage hors cadre · plan large" : "Personne hors cadre · plan large";
        } else {
            State = mode switch {
                FramingMode.Body => "Suivi de la personne",
                FramingMode.UpperBody => "Suivi du buste",
                _ => "Suivi du visage"
            };
        }
        float step = lastFrame == 0 ? 0 : (float)(1 - Math.Exp(-Math.Min(now - lastFrame, 100) / 390.0));
        lastFrame = now;
        // Widen a little faster than we tighten to keep hands and legs in the frame.
        float zoomStep = target.Zoom < current.Zoom ? Math.Min(1, step * 1.7f) : step;
        current = new(current.X + (target.X - current.X) * step,
            current.Y + (target.Y - current.Y) * step,
            current.Zoom + (target.Zoom - current.Zoom) * zoomStep);
        return current;
    }

    static Crop Stabilize(Crop previous, Crop candidate) => new(
        Math.Abs(previous.X - candidate.X) < .025f ? previous.X : candidate.X,
        Math.Abs(previous.Y - candidate.Y) < .027f ? previous.Y : candidate.Y,
        Math.Abs(previous.Zoom - candidate.Zoom) < .05f ? previous.Zoom : candidate.Zoom);

    public void Observe(IntPtr decoder, long now) {
        if (mode == FramingMode.Off || detection is not null || now < nextDetection) return;
        nextDetection = now + 150;
        if (mode == FramingMode.Face) {
            if (Native.TrackingPreview(decoder, luminance, Width, Height)) detection = ReadFace();
        } else if (Native.PosePreview(decoder, rgb, BodyPose.Size, BodyPose.PictureHeight)) {
            bool fullBody = mode == FramingMode.Body;
            detection = Task.Run(() => pose.Find(rgb, fullBody));
        }
    }

    internal async Task<Crop?> ReadFace(byte[]? testFrame = null) {
        if (!FaceDetector.IsSupported || !FaceDetector.IsBitmapPixelFormatSupported(BitmapPixelFormat.Gray8))
            throw new PlatformNotSupportedException();
        using var image = new SoftwareBitmap(BitmapPixelFormat.Gray8, Width, Height);
        using var writer = new DataWriter();
        writer.WriteBytes(testFrame ?? luminance);
        image.CopyFromBuffer(writer.DetachBuffer());
        detector ??= await FaceDetector.CreateAsync();
        var faces = await detector.DetectFacesAsync(image);
        if (faces.Count == 0) return null;
        var face = faces.Select(item => item.FaceBox).MaxBy(box => box.Height * box.Width);
        float zoom = Math.Clamp(.32f * Height / face.Height, 1.08f, 1.50f);
        float half = .5f / zoom;
        return new(Math.Clamp((face.X + face.Width * .5f) / Width, half, 1 - half),
            Math.Clamp((face.Y + face.Height * .78f) / Height + .08f, half, 1 - half), zoom);
    }
}