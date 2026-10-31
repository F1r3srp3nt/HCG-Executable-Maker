# HCG branding assets

**No official HCG logo has been provided yet.** The sidebar currently shows a clearly-marked text placeholder
(`Controls/BrandMark.axaml`), which is *not* the official logo and must not be represented as such.

To install the approved logo:

1. Export the official mark as a transparent PNG, ideally 512×512 px (square).
2. Save it as `src/HCG.ExecutableMaker.App/Assets/Branding/hcg-logo.png`.
3. Rebuild. `BrandMark` detects the file automatically and replaces the placeholder.

The application window/dock icon should also use the official asset once provided (M0.2 task).
