using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CadreLink;

public sealed class Glass : ShaderEffect {
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
        nameof(Input), typeof(Glass), 0);
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    public Glass() {
        PixelShader = new PixelShader { UriSource = new Uri(
            $"/{typeof(Glass).Assembly.GetName().Name};component/Assets/Glass.ps", UriKind.Relative) };
        UpdateShaderValue(InputProperty);
    }
}