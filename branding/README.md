# HCG branding

| File | What it is |
|---|---|
| `hcg-logo-temporary-original.png` | **Temporary official HCG logo**, as provided by the founder (1933×814, opaque black background). Kept unmodified. Not compiled into the app. |
| `../src/HCG.ExecutableMaker.App/Assets/Branding/hcg-logo.png` | Derived wordmark used in the sidebar and About card (transparent background, cropped, 720 px wide). |
| `../src/HCG.ExecutableMaker.App/Assets/Branding/hcg-app-icon.png` / `.ico` | Derived square application icon (window/dock icon, Windows `.exe` icon). |

## How the derived assets are made

The artwork is light on pure black, so black is treated as "no light": alpha = the brightest colour channel and the
colour is un-premultiplied. Composited onto any dark HCG surface this reproduces the original glow without a visible
black rectangle. The image is cropped to the letterforms plus a feathered glow margin.

## Replacing the temporary logo

```bash
cp <final-logo>.png branding/hcg-logo-temporary-original.png   # or add a new file and pass its path below
python3 -I tools/branding/make_logo.py branding/hcg-logo-temporary-original.png src/HCG.ExecutableMaker.App/Assets/Branding
convert src/HCG.ExecutableMaker.App/Assets/Branding/hcg-app-icon.png \
  -define icon:auto-resize=256,128,64,48,32,24,16 src/HCG.ExecutableMaker.App/Assets/Branding/hcg-app-icon.ico
```

Requires Python 3 with numpy and Pillow, and ImageMagick for the `.ico`. If the final logo has a transparent background
or is not light-on-black, copy it to `Assets/Branding/hcg-logo.png` directly instead of using the script.
A square variant of the mark designed for small sizes would make a better app icon than the derived one.
