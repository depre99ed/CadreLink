using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CadreLink;

internal sealed class BodyPose {
    internal const int Size = 192, Channels = 4, PictureHeight = 108, Padding = (Size - PictureHeight) / 2;
    const int KeypointCount = 17;
    internal record struct Point(float X, float Y, float Score) {
        public bool Visible(float minimum) =>
            Score >= minimum && X is >= 0 and <= 1 && Y is >= 0 and <= 1;
    }

    static readonly Lazy<InferenceSession> Model = new(() => {
        string path = Path.Combine(AppContext.BaseDirectory, "pose-lightning.onnx");
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        return new InferenceSession(path, options);
    });

    internal AutoFraming.Crop? Find(byte[] rgba, bool fullBody) {
        var model = Model.Value;
        int[] dims = [1, Size, Size, Channels];
        var tensor = NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<byte>(rgba, dims));
        using var result = model.Run(new[] { tensor });
        var points = result.Single().AsTensor<float>().ToArray();
        if (points.Length != KeypointCount * 3) return null;
        var body = new Point[KeypointCount];
        for (int i = 0; i < KeypointCount; ++i) {
            int channel = i * 3;
            body[i] = new(points[channel + 1], (points[channel] * Size - Padding) / PictureHeight, points[channel + 2]);
        }
        return Frame(body, fullBody);
    }

    internal static AutoFraming.Crop? Frame(Point[] joints, bool fullBody) {
        bool Shoulder(int i) => joints[i].Visible(.34f);
        if (!Shoulder(5) && !Shoulder(6)) return null;

        var torso = joints.Skip(5).Take(8).Where(joint => joint.Visible(.30f)).ToArray();
        if (torso.Length < 3 || torso.Max(joint => joint.Y) - torso.Min(joint => joint.Y) < .085f)
            return null;

        bool legsSeen = joints[13..].Count(joint => joint.Visible(.30f)) >= 2;
        var included = fullBody && legsSeen ? joints : joints[..13];
        var visible = included.Where(joint => joint.Visible(.30f)).ToArray();
        float left = visible.Min(joint => joint.X), right = visible.Max(joint => joint.X);
        float shoulderTop = Shoulder(5) && Shoulder(6) ? Math.Min(joints[5].Y, joints[6].Y) :
            Shoulder(5) ? joints[5].Y : joints[6].Y;
        float headRoom = Math.Clamp((torso.Max(joint => joint.Y) - shoulderTop) * .48f, .075f, .20f);
        float top = Math.Max(0, Math.Min(visible.Min(joint => joint.Y) - .045f, shoulderTop - headRoom));
        float bottom = Math.Min(1, visible.Max(joint => joint.Y) + .072f);
        float zoom = Math.Clamp(Math.Min(1f / ((right - left) * 1.18f + .14f),
            1f / ((bottom - top) * 1.14f + .06f)), 1f, 1.55f);
        float half = .5f / zoom;
        return new(Math.Clamp((left + right) * .5f, half, 1 - half),
            Math.Clamp((top + bottom) * .5f, half, 1 - half), zoom);
    }
}