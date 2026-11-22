using Foundation;
using UIKit;

namespace HelloDotNetIos;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        var controller = new UIViewController();
        controller.View!.BackgroundColor = UIColor.SystemBackground;
        var label = new UILabel(controller.View.Bounds)
        {
            Text = "Hello from .NET for iOS",
            TextAlignment = UITextAlignment.Center,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
        };
        controller.View.AddSubview(label);
        Window.RootViewController = controller;
        Window.MakeKeyAndVisible();
        return true;
    }
}
