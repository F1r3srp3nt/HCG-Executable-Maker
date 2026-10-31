using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace HCG.ExecutableMaker.App.Controls;

public partial class BrandMark : UserControl
{
    public static readonly Uri OfficialLogoUri = new("avares://HCGExecutableMaker/Assets/Branding/hcg-logo.png");

    public BrandMark()
    {
        AvaloniaXamlLoader.Load(this);
        if (AssetLoader.Exists(OfficialLogoUri))
        {
            var image = this.FindControl<Image>("OfficialLogo")!;
            image.Source = new Bitmap(AssetLoader.Open(OfficialLogoUri));
            image.IsVisible = true;
            this.FindControl<Border>("Placeholder")!.IsVisible = false;
        }
    }
}
