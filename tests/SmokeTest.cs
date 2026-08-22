using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using MacroDeck;

// Exercises LibraryScanner, Settings, IconFactory and SwpVersionReader
// (everything that runs outside SolidWorks) against the 0.6.0 two-level
// library format.
internal static class SmokeTest
{
    private static int _failures;

    private static void Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what);
        if (!condition)
        {
            _failures++;
        }
    }

    private static void Macro(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "x");
    }

    private static void Image(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        using (Bitmap bmp = new Bitmap(32, 32))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.FillRectangle(Brushes.Red, 8, 8, 16, 16);
            }
            bmp.Save(Path.Combine(dir, fileName), System.Drawing.Imaging.ImageFormat.Bmp);
        }
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "macrodeck_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // --- valid: one macro in its own folder = plain button ---
        string dxf = Path.Combine(root, "Save As DXF");
        Macro(dxf, "SaveAsDxf v2.1.swp");
        Image(dxf, "icon.bmp");
        File.WriteAllText(Path.Combine(dxf, "description.md"),
            "# Save As DXF\r\n\r\n- Exports the *active* part\r\nas a DXF file.\r\n");
        File.WriteAllText(Path.Combine(dxf, "readme.md"), "Internal notes, not a description.");
        Directory.CreateDirectory(Path.Combine(dxf, "old versions")); // level 3: never examined

        // --- valid: macro folders = drop-down ---
        string sheet = Path.Combine(root, "Sheet Metal");
        Directory.CreateDirectory(sheet);
        Image(sheet, "icon.png");
        File.WriteAllText(Path.Combine(sheet, "description.txt"), "Sheet metal helpers.");
        Macro(Path.Combine(sheet, "Flatten All"), "flatten_0.3.swp");
        File.WriteAllText(Path.Combine(sheet, "Flatten All", "description.md"), "Flattens every body.");
        Image(Path.Combine(sheet, "Flatten All"), "whatever.bmp"); // any image works
        Macro(Path.Combine(sheet, "Export Flat"), "export.swb");
        Macro(Path.Combine(sheet, "Broken Entry"), "one.swp");
        File.WriteAllText(Path.Combine(sheet, "Broken Entry", "two.swp"), "x"); // 2 macros = skipped

        // --- malformed and ignored folders ---
        string flat = Path.Combine(root, "Old Style Group");
        Macro(flat, "a.swp");
        File.WriteAllText(Path.Combine(flat, "b.swp"), "x"); // 2 loose macros
        string mixed = Path.Combine(root, "Mixed");
        Macro(mixed, "loose.swp");
        Macro(Path.Combine(mixed, "Nested"), "nested.swp"); // macro + macro folder
        Image(Path.Combine(root, "Just Pictures"), "icon.bmp"); // no macros: silently ignored
        File.WriteAllText(Path.Combine(root, "LooseInRoot.swp"), "x");

        ScanResult scan = LibraryScanner.Scan(root);
        List<MacroButton> buttons = scan.Buttons;

        Check(buttons.Count == 2, "2 valid buttons, got " + buttons.Count
            + " (" + string.Join(", ", buttons.Select(b => b.Name).ToArray()) + ")");
        Check(buttons[0].Name == "Save As DXF", "plain button named after its folder");
        Check(!buttons[0].IsMulti && buttons[0].Macros.Count == 1, "plain button holds one macro");
        Check(buttons[0].Macros[0].DisplayName == "Save As DXF",
            "plain button's macro takes the folder name, not the file name (got "
            + buttons[0].Macros[0].DisplayName + ")");
        Check(buttons[0].Description == "Save As DXF Exports the *active* part as a DXF file.",
            "description.md read and flattened, got: " + buttons[0].Description);
        Check(buttons[0].IconPath != null && buttons[0].IconPath.EndsWith("icon.bmp"),
            "icon.bmp picked up");

        MacroButton sheetBtn = buttons[1];
        Check(sheetBtn.Name == "Sheet Metal" && sheetBtn.IsMulti, "drop-down button from macro folders");
        Check(sheetBtn.Macros.Count == 2, "2 valid entries (Broken Entry excluded), got " + sheetBtn.Macros.Count);
        Check(sheetBtn.Macros[0].DisplayName == "Export Flat" && sheetBtn.Macros[1].DisplayName == "Flatten All",
            "entries named by folder, sorted alphabetically");
        Check(sheetBtn.Macros[1].Description == "Flattens every body.", "entry description from its own folder");
        Check(sheetBtn.Macros[1].IconPath != null && sheetBtn.Macros[1].IconPath.EndsWith("whatever.bmp"),
            "any single image in a macro folder is its icon");
        Check(sheetBtn.Macros[0].IconPath == null, "macro folder without an image has no icon");
        Check(sheetBtn.Description == "Sheet metal helpers.", "description.txt also accepted");

        Check(scan.Skipped.Count == 4, "4 skipped folders, got " + scan.Skipped.Count
            + " (" + string.Join(" | ", scan.Skipped.Select(s => Path.GetFileName(s.Path)).ToArray()) + ")");
        Check(scan.Skipped.Any(s => s.Path == flat && s.Reason.Contains("2 macro files in one folder")),
            "flat multi-macro folder reported");
        Check(scan.Skipped.Any(s => s.Path == mixed && s.Reason.Contains("both")),
            "mixed macro + macro-folder reported");
        Check(scan.Skipped.Any(s => s.Path.EndsWith("Broken Entry")),
            "macro folder with 2 macros reported");
        Check(scan.Skipped.Any(s => s.Path == root && s.Reason.Contains("directly in the library folder")),
            "loose macro in the library root reported");
        Check(!scan.Skipped.Any(s => s.Path.EndsWith("Just Pictures")),
            "folder without macros is ignored silently, not reported");

        Check(LibraryScanner.Scan(null).Buttons.Count == 0, "null path scans to nothing");
        Check(LibraryScanner.Scan(Path.Combine(root, "nope")).Buttons.Count == 0, "missing path scans to nothing");

        // --- multi-library merge, prefs, ordering ---
        Settings.SettingsPathOverride = Path.Combine(root, "settings_test.json");
        string lib2 = Path.Combine(Path.GetTempPath(), "macrodeck_smoke2_" + Guid.NewGuid().ToString("N"));
        Macro(Path.Combine(lib2, "Zip Export"), "zip.swp");

        ScanResult merged = LibraryScanner.ScanAll(new string[] { root, lib2, Path.Combine(root, "nope") });
        Check(merged.Buttons.Count == 3, "ScanAll merges libraries, got " + merged.Buttons.Count);
        Check(merged.Buttons.All(b => b.FolderPath != null && b.LibraryPath != null),
            "FolderPath and LibraryPath set");

        SettingsData settings = new SettingsData();
        List<MacroButton> ordered = LibraryScanner.ApplyPrefs(merged.Buttons, settings);
        Check(ordered.Select(b => b.Name).SequenceEqual(
            new string[] { "Save As DXF", "Sheet Metal", "Zip Export" }), "auto mode: alphabetical");
        Check(ordered.All(b => b.Enabled) && ordered.All(b => b.Macros.All(m => m.Enabled)),
            "everything enabled by default");

        // switch a macro off -> drop-down collapses toward a plain button
        MacroButton sheetMerged = ordered.First(b => b.Name == "Sheet Metal");
        Settings.GetOrCreateMacroPref(settings, sheetMerged.Macros[0].FolderPath).Enabled = false;
        ordered = LibraryScanner.ApplyPrefs(merged.Buttons, settings);
        sheetMerged = ordered.First(b => b.Name == "Sheet Metal");
        Check(sheetMerged.IsMulti, "folder still counts as multi for the manager");
        Check(sheetMerged.EnabledMacros.Count == 1, "only one macro left enabled");
        Check(sheetMerged.EnabledMacros[0].DisplayName == "Flatten All", "the surviving macro is the right one");

        // custom order + newcomer appends at the end
        settings.OrderCustomized = true;
        Settings.GetOrCreatePref(settings, ordered[2].FolderPath).Order = 0;
        Settings.GetOrCreatePref(settings, ordered[1].FolderPath).Order = 1;
        Settings.GetOrCreatePref(settings, ordered[0].FolderPath).Order = 2;
        Macro(Path.Combine(lib2, "Align Parts"), "align.swp");
        merged = LibraryScanner.ScanAll(new string[] { root, lib2 });
        ordered = LibraryScanner.ApplyPrefs(merged.Buttons, settings);
        Check(ordered.Select(b => b.Name).SequenceEqual(
            new string[] { "Zip Export", "Sheet Metal", "Save As DXF", "Align Parts" }),
            "custom order kept, newcomer appended, got: "
            + string.Join(", ", ordered.Select(b => b.Name).ToArray()));

        // library on/off
        Settings.SetLibraryEnabled(settings, lib2, false);
        Check(!Settings.IsLibraryEnabled(settings, lib2), "library switched off");
        Check(Settings.IsLibraryEnabled(settings, root), "other library unaffected");
        Check(ordered.Count(b => Settings.IsLibraryEnabled(settings, b.LibraryPath)) == 2,
            "2 buttons remain visible with lib2 off");

        // settings round-trip
        settings.Libraries.Add(root);
        settings.Libraries.Add(lib2);
        Settings.Save(settings);
        SettingsData reloaded = Settings.Load();
        Check(reloaded.Libraries.Count == 2, "round-trip: libraries");
        Check(!Settings.IsLibraryEnabled(reloaded, lib2), "round-trip: disabled library");
        Check(reloaded.OrderCustomized, "round-trip: customized flag");
        Check(reloaded.Buttons.ContainsKey(root.ToUpperInvariant() + "\\SAVE AS DXF"),
            "round-trip: button keys are case-insensitive");
        Check(reloaded.Macros.Count == 1 && !reloaded.Macros.Values.First().Enabled,
            "round-trip: per-macro toggle");

        Directory.Delete(lib2, true);

        // --- icon pipeline ---
        string iconDir = Path.Combine(root, "icons");
        Directory.CreateDirectory(iconDir);
        Bitmap user = IconFactory.LoadUserIcon(buttons[0].IconPath);
        Check(user != null, "user BMP loads");
        Check(user.GetPixel(0, 0).A == 0, "BMP corner colour became transparent");
        Check(user.GetPixel(16, 16).A == 255, "BMP artwork stayed opaque");

        List<Bitmap> icons = new List<Bitmap>();
        icons.Add(user);
        icons.Add(IconFactory.MakeTileIcon("Sheet Metal"));
        icons.Add(IconFactory.MakeLibraryIcon());
        string[] strips = IconFactory.SaveStrips(icons, iconDir, "test");
        Check(strips.Length == 6, "6 strip sizes produced");
        int[] sizes = { 20, 32, 40, 64, 96, 128 };
        for (int i = 0; i < strips.Length; i++)
        {
            using (Bitmap strip = new Bitmap(strips[i]))
            {
                Check(strip.Width == sizes[i] * icons.Count && strip.Height == sizes[i],
                    "strip " + sizes[i] + " is " + strip.Width + "x" + strip.Height);
            }
        }
        foreach (Bitmap b in icons)
        {
            b.Dispose();
        }

        // Premultiplied scaling: a white glyph must not pick up a grey cast.
        using (Bitmap white = new Bitmap(128, 128, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(white))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Color.White, 8f))
                {
                    g.DrawEllipse(pen, 12, 12, 104, 104);
                }
            }
            List<Bitmap> whiteList = new List<Bitmap>();
            whiteList.Add(white);
            string[] whiteStrips = IconFactory.SaveStrips(whiteList, iconDir, "white");
            using (Bitmap small = new Bitmap(whiteStrips[1]))
            {
                int edge = 0;
                int dark = 0;
                for (int y = 0; y < small.Height; y++)
                {
                    for (int x = 0; x < small.Width; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        if (c.A > 30 && c.A < 220)
                        {
                            edge++;
                            if (c.R < 200 || c.G < 200 || c.B < 200)
                            {
                                dark++;
                            }
                        }
                    }
                }
                Check(edge > 0, "white-ring test produced edge pixels (" + edge + ")");
                Check(dark == 0, "no grey bleed into white edges (" + dark + " of " + edge + ")");
            }
        }

        CheckVersionReader(root);
        CheckUpdateChecker();

        Directory.Delete(root, true);
        Console.WriteLine(_failures == 0 ? "ALL PASSED" : (_failures + " FAILURES"));
        return _failures;
    }

    // --- reading a macro's version out of its .swp ---
    //
    // The parts that can be tested without a real macro file: the MS-OVBA
    // decompressor, the dir-stream walk, and the promise that nothing here
    // ever throws. Reading real .swp files is verified separately against
    // oletools - see docs\DEVELOPMENT.md section 7.9.

    // A hand-built MS-OVBA container holding the text in SampleText below.
    // Small enough to embed, and full of repeats so that copy tokens at more
    // than one offset/length split are exercised rather than literals only.
    private static readonly byte[] SampleContainer = {
        0x01, 0x90, 0xB0, 0x04, 0x27, 0x3D, 0x4A, 0x00, 0x0D, 0x0A, 0x27, 0x20,
        0x55, 0x00, 0x49, 0x20, 0x2D, 0x20, 0x44, 0x61, 0x72, 0x6B, 0x03, 0x00,
        0x18, 0x01, 0x1E, 0x20, 0x20, 0x56, 0x65, 0x72, 0x73, 0x08, 0x69, 0x6F,
        0x6E, 0x00, 0x12, 0x30, 0x2E, 0x31, 0x2E, 0xC2, 0x30, 0x03, 0x28, 0x44,
        0x61, 0x74, 0x65, 0x00, 0x36, 0x00, 0x1E, 0x00, 0x32, 0x30, 0x32, 0x36,
        0x2D, 0x30, 0x38, 0x2D, 0x04, 0x30, 0x37, 0x03, 0x2E, 0x41, 0x75, 0x74,
        0x68, 0x6F, 0x02, 0x72, 0x01, 0x1B, 0x4A, 0x61, 0x6D, 0x65, 0x73, 0x20,
        0x40, 0x44, 0x65, 0x62, 0x6F, 0x6E, 0x6F, 0x03, 0x4A, 0x4C, 0x20, 0x69,
        0x63, 0x65, 0x6E, 0x63, 0x01, 0x38, 0x4D, 0x49, 0x02, 0x54, 0x00, 0x67,
        0x66, 0x75, 0x6C, 0x6C, 0x20, 0x74, 0x00, 0x65, 0x78, 0x74, 0x20, 0x62,
        0x65, 0x6C, 0x6F, 0x06, 0x77, 0x00, 0x7F, 0x4D, 0xD0, 0x0D, 0x0A, 0x4F,
        0x70, 0x74, 0x01, 0x81, 0x5E, 0x45, 0x78, 0x70, 0x6C, 0x69, 0x63, 0x69,
        0x00, 0x74, 0x0D, 0x0A
    };

    private const string SampleText =
        "'==============================================================================\r\n"
        + "' UI - Dark\r\n"
        + "'\r\n"
        + "'   Version   0.1.0\r\n"
        + "'   Date      2026-08-07\r\n"
        + "'   Author    James Debono\r\n"
        + "'   Licence   MIT - full text below\r\n"
        + "'==============================================================================\r\n"
        + "\r\n"
        + "Option Explicit\r\n";

    private static void CheckVersionReader(string root)
    {
        byte[] plain = SwpVersionReader.Decompress(SampleContainer, 0);
        string text = plain == null ? null : Encoding.ASCII.GetString(plain);
        Check(text == SampleText, "MS-OVBA container decompresses byte for byte ("
            + (plain == null ? "null" : plain.Length + " of " + SampleText.Length + " bytes") + ")");
        Check(SwpVersionReader.Decompress(new byte[] { 0x02, 0x00, 0x00 }, 0) == null,
            "a buffer with no 0x01 signature is not treated as compressed");

        // The dir walk. PROJECTVERSION declares a size of 4 but carries 6
        // bytes, so a walk that believes the size field ends up two bytes out
        // of step and finds no modules at all after it - which is exactly what
        // happened the first time. Everything after PROJECTVERSION here is
        // there to catch that regression.
        List<byte> dir = new List<byte>();
        AddRecord(dir, 0x0009, new byte[] { 0x45, 0x14, 0x9C, 0x6C, 0x00, 0x00 }, 4); // lies: says 4
        AddRecord(dir, 0x001A, Encoding.ASCII.GetBytes("mbcsname"), -1);
        AddRecord(dir, 0x0032, Encoding.Unicode.GetBytes("module1"), -1);
        AddRecord(dir, 0x0031, new byte[] { 0xD2, 0x04, 0x00, 0x00 }, -1);            // offset 1234
        AddRecord(dir, 0x001A, Encoding.ASCII.GetBytes("UserForm1"), -1);
        AddRecord(dir, 0x0031, new byte[] { 0x39, 0x30, 0x00, 0x00 }, -1);            // offset 12345

        List<SwpVersionReader.ModuleEntry> modules = SwpVersionReader.ParseDir(dir.ToArray());
        Check(modules.Count == 2, "dir walk survives PROJECTVERSION's short size field, got "
            + modules.Count + " module(s)");
        if (modules.Count == 2)
        {
            Check(modules[0].StreamName == "module1",
                "the Unicode stream name wins over the MBCS one, got " + modules[0].StreamName);
            Check(modules[0].TextOffset == 1234, "first module's source offset, got " + modules[0].TextOffset);
            Check(modules[1].StreamName == "UserForm1",
                "a module with no Unicode name still reads, got " + modules[1].StreamName);
            Check(modules[1].TextOffset == 12345, "second module's source offset, got " + modules[1].TextOffset);
        }
        Check(SwpVersionReader.ParseDir(new byte[] { 0x31, 0x00, 0xFF, 0xFF, 0xFF, 0x7F }).Count == 0,
            "a record claiming more bytes than exist stops the walk instead of reading past it");

        // The accepted shape of a version line. This is a documented format
        // that other people's macros are invited to adopt, so what counts and
        // what does not is pinned here rather than left to the regex.
        CheckVersionLine("'   Version   0.11.2", "0.11.2", "the house style");
        CheckVersionLine("' Version 1.0.0", "1.0.0", "one space either side");
        CheckVersionLine("'Version 1.0.0", "1.0.0", "no space after the apostrophe");
        CheckVersionLine("' version 1.0.0", "1.0.0", "lower case");
        CheckVersionLine("' VERSION 1.0.0", "1.0.0", "upper case");
        CheckVersionLine("    ' Version 1.0.0", "1.0.0", "an indented comment");
        CheckVersionLine("' Version: 1.0.0", "1.0.0", "a colon after the word");
        CheckVersionLine("' Version:1.0.0", "1.0.0", "a colon and no space");
        CheckVersionLine("' Version 12.345.6789", "12.345.6789", "multi-digit parts");
        CheckVersionLine("' Version 1.0", "1.0", "two parts, as older macros are often numbered");
        CheckVersionLine("' Version 2.11", "2.11", "two parts, multi-digit");
        CheckVersionLine("' Version 1.0.0.5", "1.0.0.5", "four parts, marking a test build");

        CheckVersionLine("' Version 1", null, "a bare number is too weak to be a version");
        CheckVersionLine("' Version 1.0.0.5.7", null, "five parts is not a version anybody means");
        CheckVersionLine("' Version 1.0.0 (beta)", null, "trailing text");
        CheckVersionLine("' Version 1.0.0 - first release", null, "a trailing comment");
        CheckVersionLine("' Versions 1.0.0", null, "a different word");
        CheckVersionLine("' Version1.0.0", null, "no separator at all");
        CheckVersionLine("Version 1.0.0", null, "not a comment line");
        CheckVersionLine("Const MACRO_VERSION = \"1.0.0\" ' Version 1.0.0", null,
            "a version line that is not the whole line");
        // Real prose from an archived macro. It is why a digit must follow the
        // word rather than merely something.
        CheckVersionLine("' version of swconst, so that one is read instead", null,
            "prose that happens to begin with the word");

        // Never throws, whatever it is handed. A macro whose version cannot be
        // read shows nothing, which is the right answer for somebody else's.
        Check(SwpVersionReader.Read(null) == null, "null path reads as no version");
        Check(SwpVersionReader.Read("") == null, "empty path reads as no version");
        Check(SwpVersionReader.Read(Path.Combine(root, "no such file.swp")) == null,
            "missing file reads as no version");
        string notCompound = Path.Combine(root, "notreally.swp");
        File.WriteAllText(notCompound, "this is not an OLE compound file");
        Check(SwpVersionReader.Read(notCompound) == null, "a non-compound .swp reads as no version");

        // The scanner's placeholder macros are one-byte text files, so the
        // whole pass must come back empty without complaint.
        ScanResult scan = LibraryScanner.Scan(root);
        SwpVersionReader.FillVersions(scan.Buttons);
        Check(scan.Buttons.All(b => b.Macros.All(m => m.Version == null)),
            "FillVersions leaves unreadable macros blank rather than failing");
        SwpVersionReader.FillVersions(null); // must not throw
        Check(true, "FillVersions tolerates a null list");
    }

    // --- checking for updates ---
    //
    // The network is not exercised here. What is: reading GitHub's reply, and
    // deciding whether the released version is newer than the installed one.
    // Getting that comparison wrong would either nag people who are already up
    // to date or stay silent when there is genuinely something to fetch.
    private static void CheckUpdateChecker()
    {
        // GitHub's release payload, trimmed to the fields that matter. Extra
        // fields must be ignored rather than upset the parse.
        string payload = "{\"url\":\"https://api.github.com/repos/x/y/releases/1\","
            + "\"tag_name\":\"v1.2.3\",\"name\":\"MacroDeck 1.2.3\",\"draft\":false,"
            + "\"prerelease\":false,\"assets\":[{\"name\":\"MacroDeck-1.2.3.msi\",\"size\":1179648}],"
            + "\"body\":\"Notes with \\\"quotes\\\" and a } brace\"}";
        Check(UpdateChecker.ParseTagName(payload) == "v1.2.3",
            "tag_name read from a realistic release payload, got "
            + UpdateChecker.ParseTagName(payload));
        Check(UpdateChecker.ParseTagName("{\"message\":\"Not Found\"}") == null,
            "a reply with no tag_name reads as nothing");
        Check(UpdateChecker.ParseTagName("not json at all") == null,
            "a reply that is not JSON reads as nothing rather than throwing");
        Check(UpdateChecker.ParseTagName("") == null, "an empty reply reads as nothing");
        Check(UpdateChecker.ParseTagName(null) == null, "a null reply reads as nothing");

        Check(UpdateChecker.StripLeadingV("v1.2.3") == "1.2.3", "a leading v is dropped");
        Check(UpdateChecker.StripLeadingV("1.2.3") == "1.2.3", "no v to drop is fine");
        Check(UpdateChecker.StripLeadingV("V0.7.0") == "0.7.0", "an upper case V is dropped too");

        CheckNewer("1.0.0", "0.7.0", true, "a released 1.0.0 beats 0.7.0");
        CheckNewer("0.8.0", "0.7.0", true, "a minor bump is newer");
        CheckNewer("0.7.1", "0.7.0", true, "a patch bump is newer");
        CheckNewer("v1.0.0", "0.7.0", true, "the tag's v makes no difference");
        CheckNewer("0.7.0", "0.7.0", false, "the same version is not an update");
        CheckNewer("0.6.2", "0.7.0", false, "an older release is not an update");
        CheckNewer("0.10.0", "0.9.0", true, "10 beats 9 rather than sorting as text");
        CheckNewer("0.7.0", "0.7.0.14", false,
            "a released 0.7.0 does not nag somebody running its own test build");
        CheckNewer("0.7.1", "0.7.0.14", true,
            "but a real patch release does reach a test build");
        CheckNewer("1.0.0-beta", "0.7.0", true, "a suffixed tag still compares on its numbers");
        CheckNewer("", "0.7.0", false, "an empty tag is never an update");

        // Not every project tags releases with a version. WiX's own latest is
        // "wix3141rtm", and reading that as 0.0.0 would quietly report "up to
        // date" on the strength of a tag nobody compared.
        Check(UpdateChecker.IsComparable("1.0.0"), "1.0.0 can be compared");
        Check(UpdateChecker.IsComparable("v0.7.0"), "v0.7.0 can be compared");
        Check(UpdateChecker.IsComparable("1.0"), "1.0 can be compared");
        Check(UpdateChecker.IsComparable("1.0.0-beta"), "1.0.0-beta can be compared");
        Check(!UpdateChecker.IsComparable("wix3141rtm"), "wix3141rtm cannot be compared");
        Check(!UpdateChecker.IsComparable("nightly"), "nightly cannot be compared");
        Check(!UpdateChecker.IsComparable("release-2026"), "release-2026 cannot be compared");
        Check(!UpdateChecker.IsComparable("7"), "a bare 7 cannot be compared");
        Check(!UpdateChecker.IsComparable(""), "an empty tag cannot be compared");
        Check(!UpdateChecker.IsComparable(null), "a null tag cannot be compared");
    }

    private static void CheckNewer(string released, string installed, bool expected, string what)
    {
        bool newer = UpdateChecker.Compare(released, installed) > 0;
        Check(newer == expected, what + "   [released " + released
            + " vs installed " + installed + "] -> "
            + (newer ? "update offered" : "no update"));
    }

    private static void CheckVersionLine(string line, string expected, string what)
    {
        string got = SwpVersionReader.MatchVersionLine(line);
        Check(got == expected, (expected == null ? "rejected: " : "accepted: ") + what
            + "   [" + line + "] -> " + (got == null ? "nothing" : got));
    }

    private static void AddRecord(List<byte> dir, int id, byte[] data, int declaredSize)
    {
        int size = declaredSize >= 0 ? declaredSize : data.Length;
        dir.Add((byte)(id & 0xFF));
        dir.Add((byte)((id >> 8) & 0xFF));
        dir.Add((byte)(size & 0xFF));
        dir.Add((byte)((size >> 8) & 0xFF));
        dir.Add((byte)((size >> 16) & 0xFF));
        dir.Add((byte)((size >> 24) & 0xFF));
        dir.AddRange(data);
    }
}
