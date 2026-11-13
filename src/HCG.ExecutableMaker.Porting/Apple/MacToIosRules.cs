using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Porting.Apple;

/// <summary>Replace lines [Start..End] (inclusive) with <see cref="Lines"/>; End = Start - 1 inserts before Start.</summary>
internal sealed record LineEdit(int Start, int End, IReadOnlyList<string> Lines);

/// <summary>Result of analysing one Swift file for an iOS port.</summary>
internal sealed class SwiftFilePlan(SwiftSource source)
{
    public SwiftSource Source { get; } = source;
    public List<LineEdit> Edits { get; } = [];
    public List<PortingFinding> Findings { get; } = [];
    public HashSet<int> Covered { get; } = [];
    public bool HasBlockers => Findings.Any(f => f.Disposition == FindingDisposition.Blocker);
    public bool IsAdapted => Edits.Count > 0;

    /// <summary>The adapted text; with <paramref name="draft"/>, blocker sites get explicit compile errors for iOS.</summary>
    public string Apply(bool draft)
    {
        var edits = new List<LineEdit>(Edits);
        if (draft)
            foreach (var b in Findings.Where(f => f.Disposition == FindingDisposition.Blocker && f.Line is not null).GroupBy(f => f.Line!.Value))
            {
                var line = b.Key - 1;
                var indent = SwiftSource.Indent(Source.Lines[line]);
                var text = string.Join("; ", b.Select(f => f.Title + (f.Guidance is null ? "" : " — " + f.Guidance))).Replace("\\", "\\\\").Replace("\"", "\\\"");
                edits.Add(new LineEdit(line, line - 1, [$"{indent}#if os(iOS)", $"{indent}#error(\"HCG port blocker: {text}\")", $"{indent}#endif"]));
            }
        var lines = Source.Lines.ToList();
        foreach (var e in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            lines.RemoveRange(e.Start, Math.Max(0, e.End - e.Start + 1));
            lines.InsertRange(e.Start, e.Lines);
        }
        return string.Join('\n', lines);
    }
}

/// <summary>
/// macOS → iOS rules for SwiftUI sources. Adaptations are limited to constructs whose iOS meaning is clear; they wrap
/// macOS code in <c>#if os(macOS)</c> (so the macOS build is unchanged) or add the UIKit equivalent next to it.
/// Everything else that is macOS-only is a blocker with guidance — never replaced by a stub.
/// </summary>
internal static partial class MacToIosRules
{
    /// <summary>AppKit / macOS-only symbols with no automatic iOS adaptation, with porting guidance.</summary>
    private static readonly (string Symbol, string Guidance)[] Blocked =
    [
        ("NSOpenPanel", "Use SwiftUI .fileImporter (or UIDocumentPickerViewController) on iOS."),
        ("NSSavePanel", "Use SwiftUI .fileExporter (or UIDocumentPickerViewController for export) on iOS."),
        ("NSAlert", "Use SwiftUI .alert / .confirmationDialog."),
        ("NSWorkspace", "Use the openURL environment value or UIApplication.shared.open(_:)."),
        ("NSWindow", "iOS has no windows to manage; use scenes, sheets or NavigationStack."),
        ("NSWindowController", "Use scenes or navigation on iOS."),
        ("NSApplication", "Use UIApplication / SwiftUI scenePhase for app lifecycle on iOS."),
        ("NSApp", "Use UIApplication.shared / SwiftUI scene APIs on iOS."),
        ("NSApplicationDelegate", "Port lifecycle code to UIApplicationDelegate (@UIApplicationDelegateAdaptor) or scenePhase."),
        ("NSApplicationDelegateAdaptor", "Port lifecycle code to @UIApplicationDelegateAdaptor or scenePhase."),
        ("NSView", "Use UIView (UIViewRepresentable) or a SwiftUI view."),
        ("NSViewController", "Use UIViewController (UIViewControllerRepresentable)."),
        ("NSViewRepresentable", "Write a UIViewRepresentable equivalent."),
        ("NSViewControllerRepresentable", "Write a UIViewControllerRepresentable equivalent."),
        ("NSHostingView", "Use UIHostingController."),
        ("NSHostingController", "Use UIHostingController."),
        ("NSImage", "Use UIImage (or SwiftUI Image)."),
        ("NSColor", "Use UIColor (or SwiftUI Color)."),
        ("NSFont", "Use UIFont (or SwiftUI Font)."),
        ("NSMenu", "Use SwiftUI Menu / contextMenu."),
        ("NSMenuItem", "Use SwiftUI Menu / Button."),
        ("NSStatusBar", "Menu bar items do not exist on iOS."),
        ("NSStatusItem", "Menu bar items do not exist on iOS."),
        ("NSEvent", "Use gestures, keyboard shortcuts or UIKit events."),
        ("NSCursor", "iOS has no cursor API (pointer interactions on iPad only)."),
        ("NSTextView", "Use TextEditor or UITextView."),
        ("NSTextField", "Use TextField or UITextField."),
        ("NSButton", "Use SwiftUI Button."),
        ("NSScreen", "Use GeometryReader / UIScreen."),
        ("NSSound", "Use AVAudioPlayer / AudioServices."),
        ("NSDocument", "Use SwiftUI DocumentGroup / UIDocument."),
        ("NSDocumentController", "Use SwiftUI DocumentGroup."),
        ("NSToolbar", "Use SwiftUI .toolbar."),
        ("NSTableView", "Use SwiftUI List / Table."),
        ("NSOutlineView", "Use SwiftUI List / OutlineGroup."),
        ("NSScrollView", "Use ScrollView."),
        ("NSSplitView", "Use NavigationSplitView."),
        ("NSPopover", "Use SwiftUI .popover."),
        ("NSPanel", "Use sheets on iOS."),
        ("NSRunningApplication", "Not available on iOS."),
        ("NSAppleScript", "AppleScript is not available on iOS."),
        ("NSUserNotification", "Use UserNotifications (UNUserNotificationCenter)."),
        ("NSSharingService", "Use ShareLink / UIActivityViewController."),
        ("NSBezierPath", "Use UIBezierPath or SwiftUI Path."),
        ("NSGraphicsContext", "Use UIGraphicsImageRenderer / Core Graphics."),
        ("NSAppearance", "Use colorScheme / UITraitCollection."),
        ("NSVisualEffectView", "Use SwiftUI materials (.background(.ultraThinMaterial))."),
        ("NSAnimationContext", "Use SwiftUI animations."),
        ("NSTouchBar", "Touch Bar does not exist on iOS."),
        ("NSPasteboard", "Use UIPasteboard (HCG adapts only the plain-string copy pattern automatically)."),
        ("HSplitView", "Use NavigationSplitView or an adaptive HStack."),
        ("VSplitView", "Use a VStack or NavigationSplitView."),
        ("SettingsLink", "Present the settings view as a sheet or navigation destination on iOS."),
        ("MenuBarExtra", "Menu bar extras do not exist on iOS."),
    ];

    private static readonly string[] MacOnlyFrameworks =
        ["Carbon", "IOKit", "ServiceManagement", "ScriptingBridge", "OSAKit", "Quartz", "QuickLookUI", "InputMethodKit",
         "PreferencePanes", "SystemExtensions", "DiskArbitration", "OpenDirectory", "SecurityInterface", "AppleScriptObjC"];

    /// <summary>Scene-level modifiers that only configure macOS windows; on iOS they are dropped.</summary>
    private static readonly string[] WindowModifiers =
        ["windowStyle", "windowToolbarStyle", "windowResizability", "defaultPosition", "defaultSize", "defaultWindowPlacement",
         "windowIdealSize", "restorationBehavior", "windowBackgroundDragBehavior", "windowMinimizeBehavior", "windowDismissBehavior",
         "windowFullScreenBehavior", "windowLevel", "commandsRemoved"];

    [GeneratedRegex(@"^\s*import\s+(AppKit|Cocoa)\s*$")]
    private static partial Regex AppKitImport();

    [GeneratedRegex(@"^\s*import\s+(?<fw>\w+)\s*$")]
    private static partial Regex AnyImport();

    [GeneratedRegex(@"\b(?<sym>NS[A-Z]\w*|HSplitView|VSplitView|SettingsLink|MenuBarExtra)\b")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex(@"\bProcess\b(?!Info)\s*(\(|\.)")]
    private static partial Regex ProcessRegex();

    [GeneratedRegex(@"\.homeDirectoryForCurrentUser\b|\bHost\.current\(\)|\bopenSettings\b|\.onCommand\s*\(|\.touchBar\s*\{")]
    private static partial Regex OtherMacOnlyRegex();

    [GeneratedRegex(@"^\s*(Settings|Window|UtilityWindow|MenuBarExtra)\b\s*(\(|\{)")]
    private static partial Regex MacSceneRegex();

    [GeneratedRegex(@":\s*([\w\s,&]*\b)?App\b\s*(,|\{|where)")]
    private static partial Regex AppConformance();

    [GeneratedRegex(@"^\s*\.(?<mod>\w+)\b")]
    private static partial Regex ModifierLine();

    [GeneratedRegex(@"\bclass\s+(?<name>\w+)\s*:[^{]*\bNSApplicationDelegate\b")]
    private static partial Regex DelegateClass();

    [GeneratedRegex(@"@NSApplicationDelegateAdaptor\s*\(\s*(?<name>\w+)\.self\s*\)")]
    private static partial Regex DelegateAdaptor();

    [GeneratedRegex(@"^\s*(\}|\{|func applicationDidFinishLaunching\(_ \w+: Notification\)\s*\{|func applicationWillFinishLaunching\(_ \w+: Notification\)\s*\{|NSApp\.setActivationPolicy\(\.regular\)|NSApp\.activate\((ignoringOtherApps: true)?\)|NSApplication\.shared\.setActivationPolicy\(\.regular\)|NSApplication\.shared\.activate\((ignoringOtherApps: true)?\)|func applicationShouldTerminateAfterLastWindowClosed\(_ \w+: NSApplication\) -> Bool\s*\{\s*(return\s+)?true\s*\}|func applicationShouldTerminateAfterLastWindowClosed\(_ \w+: NSApplication\) -> Bool\s*\{|(return\s+)?true|final\s+class.*|class.*)\s*$")]
    private static partial Regex ShimLine();

    [GeneratedRegex(@"NSPasteboard\.general\.clearContents\(\)\s*$")]
    private static partial Regex PasteboardClear();

    [GeneratedRegex(@"NSPasteboard\.general\.setString\(")]
    private static partial Regex PasteboardSet();

    [GeneratedRegex(@"\b(AVAudioRecorder|AVAudioEngine|AVCaptureSession)\b")]
    private static partial Regex RecordingRegex();

    [GeneratedRegex(@"\bAVAudioSession\b")]
    private static partial Regex AudioSessionRegex();

    [GeneratedRegex(@"\bBundle\.module\b")]
    private static partial Regex BundleModule();

    public static SwiftFilePlan Analyze(SwiftSource src, bool isAppTarget)
    {
        var plan = new SwiftFilePlan(src);
        var code = src.Code;
        PortingFinding F(FindingCategory c, FindingDisposition d, string title, string detail, int line, string? guidance = null) => new()
        {
            Category = c, Disposition = d, Title = title, Detail = detail, File = src.Path, Line = line + 1,
            Evidence = src.Lines[line].Trim(), Guidance = guidance,
        };
        bool Active(int n) => src.ActiveOnIOS[n] && !src.IsDirective(n);

        // 1. Activation-shim app delegates (common in SwiftPM GUI apps): macOS-only lifecycle, unnecessary on iOS.
        for (var n = 0; n < code.Count; n++)
        {
            if (!Active(n) || DelegateClass().Match(code[n]) is not { Success: true } m) continue;
            var end = src.EndOfConstruct(n, includeModifierChain: false);
            if (end < 0) continue;
            var isShim = Enumerable.Range(n, end - n + 1).All(k => code[k].Trim().Length == 0 || ShimLine().IsMatch(code[k]));
            var name = m.Groups["name"].Value;
            var adaptors = Enumerable.Range(0, code.Count).Where(k => Active(k) && DelegateAdaptor().Match(code[k]) is { Success: true } a && a.Groups["name"].Value == name).ToList();
            if (!isShim) continue; // reported as blockers below (NSApplicationDelegate, NSApp…)
            Wrap(plan, n, end, "os(macOS)");
            foreach (var a in adaptors) Wrap(plan, a, a, "os(macOS)");
            plan.Findings.Add(F(FindingCategory.Lifecycle, FindingDisposition.AutoAdapted, $"macOS activation delegate {name} kept for macOS only",
                "This delegate only makes a SwiftPM executable behave as a regular foreground Mac app (activation policy / quit after last window). iOS apps are always foreground apps, so it is compiled only on macOS.", n));
        }

        // 2. AppKit / Cocoa imports become conditional (UIKit is imported instead where an adaptation needs it).
        var needsUIKit = false;

        // 3. Plain-string pasteboard copy → UIPasteboard.
        for (var n = 0; n < code.Count; n++)
        {
            if (!Active(n) || plan.Covered.Contains(n) || !PasteboardSet().IsMatch(code[n])) continue;
            var expr = PasteboardStringArgument(src, n);
            if (expr is null) continue;
            var start = n > 0 && PasteboardClear().IsMatch(code[n - 1]) && !plan.Covered.Contains(n - 1) ? n - 1 : n;
            var indent = SwiftSource.Indent(src.Lines[start]);
            var lines = new List<string> { indent + "#if os(macOS)" };
            lines.AddRange(src.Lines.Skip(start).Take(n - start + 1));
            lines.Add(indent + "#else");
            lines.Add($"{indent}UIPasteboard.general.string = {expr}");
            lines.Add(indent + "#endif");
            plan.Edits.Add(new LineEdit(start, n, lines));
            for (var k = start; k <= n; k++) plan.Covered.Add(k);
            needsUIKit = true;
            plan.Findings.Add(F(FindingCategory.PlatformApi, FindingDisposition.AutoAdapted, "Clipboard: NSPasteboard → UIPasteboard",
                "Copying a plain string uses UIPasteboard.general.string on iOS; the macOS code is unchanged.", start));
        }

        // 4. macOS-only scenes (Settings, Window, MenuBarExtra) in the App declaration.
        var isAppFile = Enumerable.Range(0, code.Count).Any(k => Active(k) && AppConformance().IsMatch(code[k]));
        if (isAppFile)
            for (var n = 0; n < code.Count; n++)
            {
                if (!Active(n) || plan.Covered.Contains(n) || MacSceneRegex().Match(code[n]) is not { Success: true } m) continue;
                var end = src.EndOfConstruct(n);
                if (end < 0) continue;
                Wrap(plan, n, end, "os(macOS)");
                var scene = m.Groups[1].Value;
                plan.Findings.Add(F(FindingCategory.UiAdaptation, FindingDisposition.NeedsReview, $"macOS-only scene '{scene}' is not available on iOS",
                    scene switch
                    {
                        "Settings" => "The Settings window (⌘,) does not exist on iOS. It stays in the macOS build; on iOS its view is not reachable until you expose it (e.g. as a sheet or a navigation destination). Any setting it controls keeps its default value on iOS.",
                        "MenuBarExtra" => "Menu bar extras do not exist on iOS; this scene is compiled only on macOS and its functionality is absent on iOS.",
                        _ => "Additional windows are a macOS concept; this scene is compiled only on macOS. Content shown only in it is not reachable on iOS until you present it from the main scene.",
                    }, n, "Decide how iOS users reach this content."));
            }

        // 5. Window-chrome modifiers.
        for (var n = 0; n < code.Count; n++)
        {
            if (!Active(n) || plan.Covered.Contains(n) || ModifierLine().Match(code[n]) is not { Success: true } m || !WindowModifiers.Contains(m.Groups["mod"].Value)) continue;
            var end = src.EndOfConstruct(n, includeModifierChain: false);
            if (end < 0) continue;
            Wrap(plan, n, end, "os(macOS)");
            plan.Findings.Add(F(FindingCategory.UiAdaptation, FindingDisposition.AutoAdapted, $".{m.Groups["mod"].Value} applies to macOS windows only",
                "Window styling/sizing has no iPhone equivalent; the modifier is compiled only on macOS.", n));
        }

        // 6. Imports (after we know whether UIKit is needed).
        for (var n = 0; n < code.Count; n++)
        {
            if (!Active(n)) continue;
            if (AppKitImport().Match(code[n]) is { Success: true } m)
            {
                var fw = m.Groups[1].Value;
                var lines = new List<string> { $"#if canImport({fw})", src.Lines[n] };
                if (needsUIKit) { lines.Add("#elseif canImport(UIKit)"); lines.Add("import UIKit"); }
                lines.Add("#endif");
                plan.Edits.Add(new LineEdit(n, n, lines));
                plan.Covered.Add(n);
                plan.Findings.Add(F(FindingCategory.PlatformApi, FindingDisposition.AutoAdapted, $"import {fw} made conditional",
                    needsUIKit ? $"{fw} is macOS-only; UIKit is imported on iOS for the adapted APIs." : $"{fw} is macOS-only; the import is compiled only where it exists.", n));
            }
            else if (AnyImport().Match(code[n]) is { Success: true } i && MacOnlyFrameworks.Contains(i.Groups["fw"].Value))
            {
                plan.Covered.Add(n);
                plan.Findings.Add(F(FindingCategory.PlatformApi, FindingDisposition.Blocker, $"import {i.Groups["fw"].Value}: macOS-only framework",
                    "This framework does not exist on iOS.", n, "Remove the dependency or isolate it behind #if os(macOS) with an iOS alternative."));
            }
        }

        // 7. Remaining macOS-only APIs are blockers (one finding per symbol per line).
        for (var n = 0; n < code.Count; n++)
        {
            if (!Active(n) || plan.Covered.Contains(n)) continue;
            foreach (Match m in SymbolRegex().Matches(code[n]))
            {
                var sym = m.Groups["sym"].Value;
                var rule = Blocked.FirstOrDefault(b => b.Symbol == sym);
                if (rule.Symbol is null) continue; // Foundation NS types (NSLock, NSRegularExpression…) exist on iOS
                if (plan.Findings.Any(f => f.Line == n + 1 && f.Title.StartsWith(sym + " ", StringComparison.Ordinal))) continue;
                plan.Findings.Add(F(FindingCategory.PlatformApi, FindingDisposition.Blocker, $"{sym} is macOS-only", "No supported automatic adaptation.", n, rule.Guidance));
            }
            if (ProcessRegex().IsMatch(code[n]))
                plan.Findings.Add(F(FindingCategory.PlatformApi, FindingDisposition.Blocker, "Process is unavailable on iOS",
                    "iOS apps cannot launch other programs.", n, "Move the work into the app (or a server) or drop the feature on iOS."));
            if (OtherMacOnlyRegex().Match(code[n]) is { Success: true } o)
                plan.Findings.Add(F(FindingCategory.PlatformApi, FindingDisposition.Blocker, $"{o.Value.Trim('.', '(', ' ', '{')} is macOS-only",
                    "No supported automatic adaptation.", n, o.Value.Contains("homeDirectory", StringComparison.Ordinal) ? "Use FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)." : null));
            if (isAppTarget && BundleModule().IsMatch(code[n]))
                plan.Findings.Add(F(FindingCategory.BuildSystem, FindingDisposition.Blocker, "Bundle.module in the app target",
                    "SwiftPM resource bundles of the executable target are not produced by an Xcode app target.", n,
                    "Move the resources into the Xcode app (asset catalog/resources) and load them from Bundle.main."));
        }

        // 8. Behaviour differences to review.
        var recording = Enumerable.Range(0, code.Count).FirstOrDefault(k => Active(k) && RecordingRegex().IsMatch(code[k]), -1);
        if (recording >= 0 && !code.Any(c => AudioSessionRegex().IsMatch(c)))
            plan.Findings.Add(F(FindingCategory.PlatformApi, FindingDisposition.NeedsReview, "Audio capture needs an AVAudioSession on iOS",
                "On iOS, recording requires configuring AVAudioSession (category .record or .playAndRecord) and activating it; macOS has no audio session. Without it recording fails or records silence.",
                recording, "Configure AVAudioSession under #if os(iOS) before recording."));
        return plan;
    }

    /// <summary>Extracts EXPR from <c>NSPasteboard.general.setString(EXPR, forType: .string)</c> on one line.</summary>
    internal static string? PasteboardStringArgument(SwiftSource src, int line)
    {
        var code = src.Code[line];
        var start = code.IndexOf("NSPasteboard.general.setString(", StringComparison.Ordinal);
        if (start < 0) return null;
        var open = start + "NSPasteboard.general.setString(".Length;
        int depth = 1, comma = -1, i = open;
        for (; i < code.Length && depth > 0; i++)
        {
            if (code[i] is '(' or '[' or '{') depth++;
            else if (code[i] is ')' or ']' or '}') depth--;
            else if (code[i] == ',' && depth == 1 && comma < 0) comma = i;
        }
        if (depth != 0 || comma < 0) return null;
        var second = code[(comma + 1)..(i - 1)].Trim();
        if (second != "forType: .string" || code[i..].Trim().Length > 0) return null;
        return src.Lines[line][open..comma].Trim();
    }

    private static void Wrap(SwiftFilePlan plan, int start, int end, string condition)
    {
        var src = plan.Source;
        var indent = SwiftSource.Indent(src.Lines[start]);
        var lines = new List<string> { $"{indent}#if {condition}" };
        lines.AddRange(src.Lines.Skip(start).Take(end - start + 1));
        lines.Add($"{indent}#endif");
        plan.Edits.Add(new LineEdit(start, end, lines));
        for (var k = start; k <= end; k++) plan.Covered.Add(k);
    }
}
