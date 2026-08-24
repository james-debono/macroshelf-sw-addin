// Removes the SOLIDWORKS UI records this add-in leaves behind, under either of
// its names. Run by the MSI on install and on a real uninstall.
//
// Why a compiled helper rather than a script custom action: the VBScript
// version used WMI's StdRegProv, which on 2026-08-24 was measured *reporting
// success for deletions that never happened*, and could not see a key that
// PowerShell read without trouble. Its HKEY_CURRENT_USER does not reliably
// resolve to the installing user's hive, and DeleteKey is not recursive - the
// tab records all carry GB0/GB1 group-box subkeys, so it could never have
// removed one. Microsoft.Win32.Registry has neither problem: it acts on the
// real HKCU of the calling process, and DeleteSubKeyTree is recursive.
//
// Two stores have to be cleared together, in both directions:
//
//   Custom API Toolbars\<id>     keyed by (add-in CLSID, group UserID) and
//                                holds the title. Matched on ModuleName.
//   CommandManager\<ctx>\Tab<n>  one per tab, with the button group boxes as
//                                subkeys. Matched on RefName.
//
// Leave one without the other and SOLIDWORKS has a tab with no title to draw,
// which is where stray "New Tab" rows come from.
//
// Never throws and always exits 0: a cleanup failure must not fail an install
// or an uninstall. It does always write to the log, so a failure on somebody
// else's machine is diagnosable - the previous version was silent, which is
// why it took a day to notice it did nothing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

internal static class Cleanup
{
    // Both CLSIDs are ours, so no other vendor's toolbar can match.
    private static readonly string[] OurModules =
    {
        "{1E9C2E64-7A5B-4C0D-9E3F-58A61D2B8C90}", // MacroDeck, up to 0.7.2
        "{7B3E9A21-5C48-4D6F-9E82-3A1C7F5D0B64}"  // MacroShelf, from 0.8.0
    };

    // Exact matches only. A tab called "New Tab", or anything belonging to
    // another add-in, is user space and is never touched.
    private static readonly string[] OurTabNames = { "MacroDeck", "MacroShelf" };

    private static readonly string[] Contexts =
    {
        "PartContext", "AssyContext", "DrwContext"
    };

    private static readonly StringBuilder Log = new StringBuilder();
    private static int _removed;

    // True once the settings folder has actually been deleted, so Write knows
    // not to recreate it just to log.
    private static bool _removedSettings;

    private static int Main(string[] args)
    {
        bool dryRun = Array.IndexOf(args, "--dry-run") >= 0;
        bool settings = Array.IndexOf(args, "--settings") >= 0;
        try
        {
            Run(dryRun);
            if (settings)
            {
                RemoveLegacySettingsKeys(dryRun);
                RemoveSettingsFolder(dryRun);
            }
        }
        catch (Exception ex)
        {
            Note("FAILED: " + ex.Message);
        }
        Write(dryRun);
        return 0;
    }

    // Settings written under HKCU by versions before 0.4.0, which stored a
    // single library path in the registry rather than in a JSON file. Removed
    // on a genuine uninstall only.
    //
    // NOT removed on install: Settings.Migrate still reads LibraryPath from
    // here when there is no settings.json, which is how somebody upgrading
    // from a very old build keeps their library. That path has to survive an
    // upgrade and only disappear when the product is deliberately removed.
    //
    // This is where MacroDeck 0.6.2's library came from during testing on
    // 2026-08-24, after %AppData%\MacroDeck had been migrated away: nothing
    // was left in AppData, so it fell back to LibraryPath here.
    private const string LegacySettingsKey = @"Software\MacroDeck";

    // The value names this product wrote. Only these are deleted, and the key
    // itself only goes if nothing is left in it.
    //
    // Why not just delete the key: "MacroDeck" is a name this product no longer
    // owns. Macro Deck by SuchByte is a real and widely used product, and if it
    // keeps anything under HKCU\Software\MacroDeck then removing the key would
    // destroy a stranger's settings - the same name collision that forced this
    // rename, working in the other direction. What SuchByte's app actually
    // stores there could not be checked from here, so this is written not to
    // care: anything that is not one of these three values is left alone, and
    // its presence keeps the key alive.
    private static readonly string[] LegacySettingsValues =
    {
        "LibraryPath",      // the library, read by Settings.Migrate
        "LayoutSignature",  // both written by builds before 0.4.0 and
        "UiGeneration"      // untouched by anything current
    };

    private static void RemoveLegacySettingsKeys(bool dryRun)
    {
        try
        {
            List<string> mine = new List<string>();
            List<string> theirs = new List<string>();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacySettingsKey))
            {
                if (key == null)
                {
                    Note("no legacy settings key to remove");
                    return;
                }
                foreach (string name in key.GetValueNames())
                {
                    if (Array.IndexOf(LegacySettingsValues, name) >= 0)
                    {
                        mine.Add(name);
                    }
                    else
                    {
                        theirs.Add(name);
                    }
                }
                theirs.AddRange(key.GetSubKeyNames());
            }

            foreach (string name in mine)
            {
                if (dryRun)
                {
                    Note("would remove HKCU\\" + LegacySettingsKey + "\\" + name);
                }
                else
                {
                    using (RegistryKey key =
                               Registry.CurrentUser.OpenSubKey(LegacySettingsKey, true))
                    {
                        if (key != null)
                        {
                            key.DeleteValue(name, false);
                        }
                    }
                    Note("removed HKCU\\" + LegacySettingsKey + "\\" + name);
                }
                _removed++;
            }

            if (theirs.Count > 0)
            {
                Note("kept HKCU\\" + LegacySettingsKey + ": " + theirs.Count +
                     " item(s) there are not ours (" + string.Join(", ", theirs.ToArray()) + ")");
                return;
            }

            if (dryRun)
            {
                Note("would remove the now-empty HKCU\\" + LegacySettingsKey);
                return;
            }
            Registry.CurrentUser.DeleteSubKey(LegacySettingsKey, false);
            Note("removed the now-empty HKCU\\" + LegacySettingsKey);
        }
        catch (Exception ex)
        {
            Note("could not clean HKCU\\" + LegacySettingsKey + ": " + ex.Message);
        }
    }

    // %AppData%\MacroShelf, on a genuine uninstall only - the MSI condition
    // excludes the uninstall half of an upgrade, so upgrading keeps the user's
    // library list and toggles.
    //
    // Removing it is what makes a reinstall genuinely clean rather than
    // silently inheriting the previous settings, which would otherwise mask a
    // failure of the MacroDeck-to-MacroShelf migration.
    //
    // The log normally lives in this folder, so once it is gone the log has to
    // go somewhere else - see Write.
    private static void RemoveSettingsFolder(bool dryRun)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroShelf");
        if (!Directory.Exists(dir))
        {
            Note("no settings folder to remove");
            return;
        }
        if (dryRun)
        {
            Note("would remove settings folder " + dir);
            _removed++;
            return;
        }
        try
        {
            Directory.Delete(dir, true);
            // Must be set before Write runs, or the log recreates this folder.
            _removedSettings = true;
            Note("removed settings folder " + dir);
            _removed++;
        }
        catch (Exception ex)
        {
            Note("could not remove settings folder: " + ex.Message);
        }
    }

    private static void Run(bool dryRun)
    {
        using (RegistryKey sw = Registry.CurrentUser.OpenSubKey(
                   @"Software\SolidWorks", !dryRun))
        {
            if (sw == null)
            {
                Note("no SOLIDWORKS registry key; nothing to do");
                return;
            }

            foreach (string version in sw.GetSubKeyNames())
            {
                CleanToolbars(version, dryRun);
                foreach (string context in Contexts)
                {
                    CleanTabs(version, context, dryRun);
                }
            }
        }
    }

    private static void CleanToolbars(string version, bool dryRun)
    {
        string path = @"Software\SolidWorks\" + version +
                      @"\User Interface\Custom API Toolbars";
        foreach (string name in Matching(path, "ModuleName", OurModules, true))
        {
            Delete(path, name, "toolbar " + version + "\\" + name, dryRun);
        }
    }

    private static void CleanTabs(string version, string context, bool dryRun)
    {
        string path = @"Software\SolidWorks\" + version +
                      @"\User Interface\CommandManager\" + context;
        foreach (string name in Matching(path, "RefName", OurTabNames, false))
        {
            Delete(path, name, "tab " + version + "\\" + context + "\\" + name,
                   dryRun);
        }
    }

    // Subkeys of `path` whose `valueName` matches one of `wanted`. The list is
    // materialised before anything is deleted, because deleting while
    // enumerating is undefined.
    private static List<string> Matching(string path, string valueName,
                                         string[] wanted, bool substring)
    {
        List<string> hits = new List<string>();
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
        {
            if (key == null)
            {
                return hits;
            }
            foreach (string name in key.GetSubKeyNames())
            {
                using (RegistryKey child = key.OpenSubKey(name))
                {
                    if (child == null)
                    {
                        continue;
                    }
                    string value = child.GetValue(valueName) as string;
                    if (value == null)
                    {
                        continue;
                    }
                    foreach (string w in wanted)
                    {
                        bool hit = substring
                            ? value.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0
                            : string.Equals(value, w, StringComparison.OrdinalIgnoreCase);
                        if (hit)
                        {
                            hits.Add(name);
                            break;
                        }
                    }
                }
            }
        }
        return hits;
    }

    private static void Delete(string path, string name, string label, bool dryRun)
    {
        if (dryRun)
        {
            Note("would remove " + label);
            _removed++;
            return;
        }
        try
        {
            using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(path, true))
            {
                if (parent == null)
                {
                    return;
                }
                // Recursive: the tab records carry GB0/GB1 subkeys, which is
                // exactly what defeated the previous implementation.
                parent.DeleteSubKeyTree(name, false);
            }
            // Verify rather than trust the call, since the thing this replaces
            // reported success while changing nothing.
            using (RegistryKey check = Registry.CurrentUser.OpenSubKey(path + "\\" + name))
            {
                if (check != null)
                {
                    Note("STILL PRESENT after delete: " + label);
                    return;
                }
            }
            Note("removed " + label);
            _removed++;
        }
        catch (Exception ex)
        {
            Note("could not remove " + label + ": " + ex.Message);
        }
    }

    private static void Note(string line)
    {
        Log.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
           .Append("  cleanup: ")
           .Append(line)
           .Append("\r\n");
    }

    // Where the log goes depends on what just happened.
    //
    // On install it belongs beside the settings, in %AppData%\MacroShelf, with
    // everything else the add-in writes.
    //
    // On uninstall that folder has just been deleted, and writing there would
    // recreate it - which is exactly what 0.8.0.7 did: it removed the folder,
    // then logged "removed settings folder" into a folder it had to recreate to
    // say so. The uninstall log goes to %TEMP% instead, which also means it
    // survives, so a failed uninstall on somebody else's machine can still be
    // diagnosed. Writing it into a folder that is about to vanish would have
    // been useless anyway.
    private static void Write(bool dryRun)
    {
        string summary = (dryRun ? "dry run: " : "") + _removed +
                         " item(s) removed";
        Console.Out.Write(Log.ToString());
        Console.Out.WriteLine(summary);
        try
        {
            string path = _removedSettings
                ? Path.Combine(Path.GetTempPath(), "macroshelf-uninstall.log")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MacroShelf", "macroshelf.log");

            string dir = Path.GetDirectoryName(path);
            // Only ever create the settings folder, never recreate a deleted one.
            if (!_removedSettings && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(path,
                Log.ToString() + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                "  cleanup: " + summary + "\r\n");
        }
        catch
        {
            // A log we cannot write is not worth failing over.
        }
    }
}
