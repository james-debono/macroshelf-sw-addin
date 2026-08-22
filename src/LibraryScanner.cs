using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MacroDeck
{
    // One macro: a folder containing exactly one macro file, plus its own
    // optional icon and description.
    internal class MacroCommand
    {
        public string DisplayName; // the folder's name
        public string MacroPath;
        public string FolderPath; // identity key for saved preferences
        public string Description;
        public string IconPath;
        public bool Enabled = true;

        // Read out of the .swp by SwpVersionReader, and only when the Library
        // Manager opens - never during a scan or a toolbar build. Null when the
        // macro declares no version, which is normal for somebody else's macro.
        public string Version;
    }

    // One toolbar button: either a folder holding a single macro, or a folder
    // holding several macro folders (which becomes a drop-down).
    internal class MacroButton
    {
        public string Name;
        public string FolderPath; // identity key for saved preferences
        public string LibraryPath;
        public string IconPath;
        public string Description;
        public bool Enabled = true;
        public List<MacroCommand> Macros = new List<MacroCommand>();

        // Whether the folder holds several macros, regardless of which are
        // switched on (the manager always lists them all).
        public bool IsMulti
        {
            get { return Macros.Count > 1; }
        }

        // What the toolbar actually shows: switching macros off can turn a
        // drop-down into a plain button, or remove the button entirely.
        public List<MacroCommand> EnabledMacros
        {
            get { return Macros.Where(m => m.Enabled).ToList(); }
        }
    }

    // A folder that holds macros but does not match the required layout.
    // Surfaced in the Library Manager so nothing disappears without a reason.
    internal class SkippedFolder
    {
        public string Path;
        public string Reason;
    }

    internal class ScanResult
    {
        public List<MacroButton> Buttons = new List<MacroButton>();
        public List<SkippedFolder> Skipped = new List<SkippedFolder>();
    }

    internal static class LibraryScanner
    {
        private static readonly string[] MacroExtensions = { ".swp", ".swb" };
        private static readonly string[] IconExtensions = { ".bmp", ".png" };

        // Library layout (exactly two levels are examined):
        //   Library\Button Name\macro.swp              -> a plain button
        //   Library\Button Name\Macro Name\macro.swp   -> one drop-down entry
        // Names always come from folder names. Every folder may carry its own
        // "icon.*" (or, failing that, any single image) and "description.md".
        // A folder holding macros in any other shape is reported as skipped
        // rather than guessed at; a folder holding no macros at all is simply
        // ignored.
        public static ScanResult Scan(string libraryPath)
        {
            ScanResult result = new ScanResult();
            if (string.IsNullOrEmpty(libraryPath) || !Directory.Exists(libraryPath))
            {
                return result;
            }

            try
            {
                int looseRootMacros = GetMacroFiles(libraryPath).Count;
                if (looseRootMacros > 0)
                {
                    result.Skipped.Add(NewSkipped(libraryPath,
                        looseRootMacros + " macro file(s) sit directly in the library folder - "
                        + "give each macro its own folder"));
                }
            }
            catch { }

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(libraryPath);
            }
            catch
            {
                return result;
            }

            foreach (string dir in dirs)
            {
                try
                {
                    ScanButtonFolder(dir, libraryPath, result);
                }
                catch
                {
                    // Skip folders that cannot be read.
                }
            }

            result.Buttons = result.Buttons
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }

        private static void ScanButtonFolder(string dir, string libraryPath, ScanResult result)
        {
            List<string> direct = GetMacroFiles(dir);
            List<string> macroSubFolders = Directory.GetDirectories(dir)
                .Where(sub => GetMacroFiles(sub).Count > 0)
                .OrderBy(sub => Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (direct.Count == 1 && macroSubFolders.Count == 0)
            {
                MacroButton button = NewButton(dir, libraryPath);
                MacroCommand macro = new MacroCommand();
                macro.DisplayName = button.Name;
                macro.MacroPath = direct[0];
                macro.FolderPath = dir;
                macro.IconPath = button.IconPath;
                macro.Description = button.Description;
                button.Macros.Add(macro);
                result.Buttons.Add(button);
                return;
            }

            if (direct.Count == 0 && macroSubFolders.Count > 0)
            {
                MacroButton button = NewButton(dir, libraryPath);
                foreach (string sub in macroSubFolders)
                {
                    List<string> subMacros = GetMacroFiles(sub);
                    if (subMacros.Count != 1)
                    {
                        result.Skipped.Add(NewSkipped(sub,
                            subMacros.Count + " macro files in one macro folder - "
                            + "a macro folder must contain exactly one"));
                        continue;
                    }
                    MacroCommand macro = new MacroCommand();
                    macro.DisplayName = Path.GetFileName(sub);
                    macro.MacroPath = subMacros[0];
                    macro.FolderPath = sub;
                    macro.IconPath = GetIconFile(sub);
                    macro.Description = ReadDescription(sub);
                    button.Macros.Add(macro);
                }
                if (button.Macros.Count > 0)
                {
                    result.Buttons.Add(button);
                }
                return;
            }

            if (direct.Count == 0 && macroSubFolders.Count == 0)
            {
                // No macros here at all - not an attempt at a button.
                return;
            }

            if (macroSubFolders.Count > 0)
            {
                result.Skipped.Add(NewSkipped(dir,
                    "contains both a macro file and macro folders - use one or the other"));
            }
            else
            {
                result.Skipped.Add(NewSkipped(dir,
                    direct.Count + " macro files in one folder - give each macro its own folder"));
            }
        }

        private static MacroButton NewButton(string dir, string libraryPath)
        {
            MacroButton button = new MacroButton();
            button.Name = Path.GetFileName(dir);
            button.FolderPath = dir;
            button.LibraryPath = libraryPath;
            button.IconPath = GetIconFile(dir);
            button.Description = ReadDescription(dir);
            return button;
        }

        private static SkippedFolder NewSkipped(string path, string reason)
        {
            SkippedFolder skipped = new SkippedFolder();
            skipped.Path = path;
            skipped.Reason = reason;
            return skipped;
        }

        // Scans every configured library and merges the results. A broken or
        // missing library never fails the others.
        public static ScanResult ScanAll(IEnumerable<string> libraryPaths)
        {
            ScanResult all = new ScanResult();
            if (libraryPaths == null)
            {
                return all;
            }
            foreach (string library in libraryPaths)
            {
                try
                {
                    ScanResult one = Scan(library);
                    all.Buttons.AddRange(one.Buttons);
                    all.Skipped.AddRange(one.Skipped);
                }
                catch
                {
                    // Skip unreadable libraries.
                }
            }
            return all;
        }

        // Applies saved preferences: sets Enabled on buttons and macros, and
        // returns the buttons in display order. Two ordering modes:
        // - automatic: plain alphabetical, new macros interleave;
        // - customized: saved positions win, buttons never seen before are
        //   appended at the end (alphabetical among themselves) and their
        //   position is recorded in the settings (caller saves).
        // Preferences for buttons absent from this scan are left untouched so
        // a temporarily offline library keeps its saved order and toggles.
        public static List<MacroButton> ApplyPrefs(List<MacroButton> buttons, SettingsData settings)
        {
            foreach (MacroButton button in buttons)
            {
                ButtonPref pref;
                button.Enabled = button.FolderPath != null
                    && settings.Buttons.TryGetValue(button.FolderPath, out pref)
                    ? pref.Enabled : true;
                foreach (MacroCommand macro in button.Macros)
                {
                    ButtonPref macroPref;
                    macro.Enabled = macro.FolderPath != null
                        && settings.Macros.TryGetValue(macro.FolderPath, out macroPref)
                        ? macroPref.Enabled : true;
                }
            }

            if (!settings.OrderCustomized)
            {
                return buttons.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }

            int maxOrder = -1;
            foreach (ButtonPref pref in settings.Buttons.Values)
            {
                if (pref.Order > maxOrder)
                {
                    maxOrder = pref.Order;
                }
            }

            List<MacroButton> known = new List<MacroButton>();
            List<MacroButton> newcomers = new List<MacroButton>();
            foreach (MacroButton button in buttons)
            {
                ButtonPref pref;
                if (button.FolderPath != null
                    && settings.Buttons.TryGetValue(button.FolderPath, out pref)
                    && pref.Order >= 0)
                {
                    known.Add(button);
                }
                else
                {
                    newcomers.Add(button);
                }
            }

            List<MacroButton> ordered = known
                .OrderBy(b => settings.Buttons[b.FolderPath].Order)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (MacroButton button in newcomers.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (button.FolderPath != null)
                {
                    Settings.GetOrCreatePref(settings, button.FolderPath).Order = ++maxOrder;
                }
                ordered.Add(button);
            }
            return ordered;
        }

        private static List<string> GetMacroFiles(string dir)
        {
            return Directory.GetFiles(dir)
                .Where(f => MacroExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private const int MaxDescriptionLength = 500;
        private const long MaxDescriptionFileBytes = 100 * 1024;

        // Reads "description.md" (or .txt) from the folder and flattens it into
        // a single tooltip-friendly line: markdown lead-in characters stripped,
        // whitespace collapsed, capped in length. The name is deliberately
        // fixed so that a stray readme or licence file is never picked up.
        private static string ReadDescription(string dir)
        {
            foreach (string ext in new string[] { ".md", ".txt" })
            {
                try
                {
                    string path = Path.Combine(dir, "description" + ext);
                    FileInfo info = new FileInfo(path);
                    if (!info.Exists || info.Length > MaxDescriptionFileBytes)
                    {
                        continue;
                    }
                    StringBuilder sb = new StringBuilder();
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine.Trim();
                        while (line.Length > 0 &&
                            (line[0] == '#' || line[0] == '-' || line[0] == '*' || line[0] == '>'))
                        {
                            line = line.Substring(1).TrimStart();
                        }
                        if (line.Length == 0)
                        {
                            continue;
                        }
                        if (sb.Length > 0)
                        {
                            sb.Append(' ');
                        }
                        sb.Append(line);
                        if (sb.Length >= MaxDescriptionLength)
                        {
                            break;
                        }
                    }
                    if (sb.Length == 0)
                    {
                        continue;
                    }
                    string text = sb.ToString();
                    if (text.Length > MaxDescriptionLength)
                    {
                        text = text.Substring(0, MaxDescriptionLength).TrimEnd() + "...";
                    }
                    return text;
                }
                catch
                {
                    // Fall through to the next extension (or null).
                }
            }
            return null;
        }

        // "icon.bmp"/"icon.png" if present, otherwise the first image in the
        // folder - an image can only mean one thing, so this stays forgiving.
        private static string GetIconFile(string dir)
        {
            foreach (string ext in IconExtensions)
            {
                string named = Path.Combine(dir, "icon" + ext);
                if (File.Exists(named))
                {
                    return named;
                }
            }
            foreach (string ext in IconExtensions)
            {
                string icon = Directory.GetFiles(dir)
                    .Where(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (icon != null)
                {
                    return icon;
                }
            }
            return null;
        }
    }
}
