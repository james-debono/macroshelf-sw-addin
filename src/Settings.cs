using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace MacroShelf
{
    // Preferences for one toolbar button or one macro, keyed by folder path.
    // Order is only meaningful for top-level buttons.
    internal class ButtonPref
    {
        public bool Enabled = true;
        public int Order = -1; // -1 = no saved position (auto/alphabetical)
    }

    internal class SettingsData
    {
        public List<string> Libraries = new List<string>();

        // Libraries switched off in the manager. Their buttons keep every
        // saved preference (including order slots) so switching a library back
        // on restores exactly what was there.
        public List<string> DisabledLibraries = new List<string>();

        // false: toolbar is plain alphabetical and new macros interleave.
        // true: the arrangement is frozen as saved; new macros append at the
        // end. Flips on the user's first drag in the Library Manager.
        public bool OrderCustomized;

        // What the classic toolbar was last built from: the ordered list of
        // enabled macros, hashed. SolidWorks is only told to discard the
        // toolbar's saved layout when this changes, so a toolbar the user has
        // dragged somewhere stays there across restarts. See
        // MacroShelfAddin.ToolbarSignature.
        public string ToolbarSignature;

        // Set once the 0.8.0 rename cleanup has run. The MacroDeck builds left
        // their command group registered at shutdown deliberately (0.7.2, to
        // keep the toolbar's position), so uninstalling them does not take the
        // registration away and SOLIDWORKS still draws a tab for it. The
        // cleanup removes those registrations once, on the first run after
        // upgrading. See MacroShelfAddin.CleanUpRenamedRegistrations.
        //
        // To make it run again - if a machine still shows the old tab - delete
        // this line from %AppData%\MacroShelf\settings.json, or set it false.
        public bool RenameCleanupDone;

        public Dictionary<string, ButtonPref> Buttons =
            new Dictionary<string, ButtonPref>(StringComparer.OrdinalIgnoreCase);

        // Per-macro preferences, keyed by the macro folder's path.
        public Dictionary<string, ButtonPref> Macros =
            new Dictionary<string, ButtonPref>(StringComparer.OrdinalIgnoreCase);
    }

    // Persists user preferences as JSON in %AppData%\MacroShelf\settings.json.
    internal static class Settings
    {
        public const int MaxLibraries = 10;

        // Tests point this at a scratch file so they never touch real settings.
        internal static string SettingsPathOverride;

        // Both legacy names below are historical locations written by earlier
        // versions. They are deliberately NOT renamed to MacroShelf: they name
        // where old data actually sits, so renaming them would simply look in
        // a place that has never existed and silently lose the settings.
        private const string LegacyRegistryKey = "Software\\MacroDeck";
        private const string LegacyLibraryPathValue = "LibraryPath";
        private const string LegacyFolderName = "MacroDeck";

        // Tests point this at a scratch folder so the migration never touches
        // the real %AppData%\MacroDeck.
        internal static string LegacyFolderOverride;

        public static SettingsData Load()
        {
            MigrateLegacyFolder();
            try
            {
                string path = SettingsPath();
                if (File.Exists(path))
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    SettingsData data = serializer.Deserialize<SettingsData>(File.ReadAllText(path));
                    return Normalize(data);
                }
            }
            catch (Exception ex)
            {
                LogError("Failed to load settings, using defaults: " + ex.Message);
            }
            return Normalize(Migrate());
        }

        public static void Save(SettingsData data)
        {
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                File.WriteAllText(path, serializer.Serialize(Normalize(data)));
            }
            catch (Exception ex)
            {
                LogError("Failed to save settings: " + ex.Message);
            }
        }

        public static ButtonPref GetOrCreatePref(SettingsData data, string folderPath)
        {
            return GetOrCreate(data.Buttons, folderPath);
        }

        public static ButtonPref GetOrCreateMacroPref(SettingsData data, string folderPath)
        {
            return GetOrCreate(data.Macros, folderPath);
        }

        private static ButtonPref GetOrCreate(Dictionary<string, ButtonPref> map, string folderPath)
        {
            ButtonPref pref;
            if (!map.TryGetValue(folderPath, out pref))
            {
                pref = new ButtonPref();
                map[folderPath] = pref;
            }
            return pref;
        }

        public static bool IsLibraryEnabled(SettingsData data, string libraryPath)
        {
            if (string.IsNullOrEmpty(libraryPath))
            {
                return true;
            }
            return !data.DisabledLibraries.Any(
                p => string.Equals(p, libraryPath, StringComparison.OrdinalIgnoreCase));
        }

        public static void SetLibraryEnabled(SettingsData data, string libraryPath, bool enabled)
        {
            data.DisabledLibraries.RemoveAll(
                p => string.Equals(p, libraryPath, StringComparison.OrdinalIgnoreCase));
            if (!enabled)
            {
                data.DisabledLibraries.Add(libraryPath);
            }
        }

        // Ensures every field is non-null and the preference dictionaries
        // compare paths case-insensitively (comparers do not survive a JSON
        // round-trip).
        private static SettingsData Normalize(SettingsData data)
        {
            if (data == null)
            {
                data = new SettingsData();
            }
            if (data.Libraries == null)
            {
                data.Libraries = new List<string>();
            }
            if (data.DisabledLibraries == null)
            {
                data.DisabledLibraries = new List<string>();
            }
            data.Buttons = NormalizeMap(data.Buttons);
            data.Macros = NormalizeMap(data.Macros);
            return data;
        }

        private static Dictionary<string, ButtonPref> NormalizeMap(Dictionary<string, ButtonPref> source)
        {
            Dictionary<string, ButtonPref> map =
                new Dictionary<string, ButtonPref>(StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                foreach (KeyValuePair<string, ButtonPref> entry in source)
                {
                    if (!string.IsNullOrEmpty(entry.Key) && entry.Value != null)
                    {
                        map[entry.Key] = entry.Value;
                    }
                }
            }
            return map;
        }

        // Pre-0.4.0 versions stored a single library path in the registry.
        private static SettingsData Migrate()
        {
            SettingsData data = new SettingsData();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacyRegistryKey))
                {
                    if (key != null)
                    {
                        string legacy = key.GetValue(LegacyLibraryPathValue) as string;
                        if (!string.IsNullOrEmpty(legacy))
                        {
                            data.Libraries.Add(legacy);
                        }
                    }
                }
            }
            catch { }
            return data;
        }

        // 0.8.0 renamed the add-in, which moved settings from
        // %AppData%\MacroDeck\ to %AppData%\MacroShelf\. Anyone upgrading from
        // 0.6.2 - the last build that was handed to anybody - would otherwise
        // find an empty Library Manager, so the old file is brought across on
        // first run and the old folder is then removed.
        //
        // The old folder is deleted only after the copy has been read back and
        // parsed, so a half-finished migration can never lose the settings:
        // if anything throws, the original is still there to try again next
        // time. Deleting is the documented choice - leaving nothing stale
        // behind - and costs the ability to roll back to 0.6.2 with settings
        // intact.
        internal static void MigrateLegacyFolder()
        {
            // Never migrate under a test override: the tests point
            // SettingsPathOverride at a scratch file, and without this guard a
            // test run would delete the real %AppData%\MacroDeck.
            if (!string.IsNullOrEmpty(SettingsPathOverride)
                && string.IsNullOrEmpty(LegacyFolderOverride))
            {
                return;
            }

            try
            {
                string newPath = SettingsPath();
                if (File.Exists(newPath))
                {
                    return;
                }

                string legacyDir = LegacyFolder();
                string legacyPath = Path.Combine(legacyDir, "settings.json");
                if (!File.Exists(legacyPath))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                File.Copy(legacyPath, newPath, true);

                // Read the copy back and parse it. Only a file that actually
                // deserialises counts as migrated.
                new JavaScriptSerializer()
                    .Deserialize<SettingsData>(File.ReadAllText(newPath));

                Directory.Delete(legacyDir, true);
            }
            catch (Exception ex)
            {
                LogError("Could not migrate settings from the previous name: "
                         + ex.Message);
            }
        }

        private static string LegacyFolder()
        {
            if (!string.IsNullOrEmpty(LegacyFolderOverride))
            {
                return LegacyFolderOverride;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                LegacyFolderName);
        }

        private static string SettingsPath()
        {
            if (!string.IsNullOrEmpty(SettingsPathOverride))
            {
                return SettingsPathOverride;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MacroShelf", "settings.json");
        }

        private static void LogError(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroShelf");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "macroshelf.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
            }
            catch { }
        }
    }
}
