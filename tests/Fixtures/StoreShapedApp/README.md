# StoreShapedApp

A public fixture with HCG Store Manager's package shape (HCG Store Manager itself is a private repository). The `store-manager-shape` CI job builds it through the real New Build wizard. It verifies:
- the app product is the one `AppPackaging.json` names, never the command-line tool;
- `AppIcon.icns`, the Info.plist template and the artwork are in the bundle, and the app is fully sealed;
- APP, ZIP, DMG and PKG are produced;
- the GUI launches and finds its artwork through `Bundle.main`.
