using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace MacroDeck
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
        // MacroDeckAddin.ToolbarSignature.
        public string ToolbarSignature;

        public Dictionary<string, ButtonPref> Buttons =
            new Dictionary<string, ButtonPref>(StringComparer.OrdinalIgnoreCase);

        // Per-macro preferences, keyed by the macro folder's path.
        public Dictionary<string, ButtonPref> Macros =
            new Dictionary<string, ButtonPref>(StringComparer.OrdinalIgnoreCase);
    }

    // Persists user preferences as JSON in %AppData%\MacroDeck\settings.json.
    internal static class Settings
    {
        public const int MaxLibraries = 10;

        // Tests point this at a scratch file so they never touch real settings.
        internal static string SettingsPathOverride;

        private const string LegacyRegistryKey = "Software\\MacroDeck";
        private const string LegacyLibraryPathValue = "LibraryPath";

        public static SettingsData Load()
        {
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

        private static string SettingsPath()
        {
            if (!string.IsNullOrEmpty(SettingsPathOverride))
            {
                return SettingsPathOverride;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MacroDeck", "settings.json");
        }

        private static void LogError(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroDeck");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "macrodeck.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
            }
            catch { }
        }
    }
}
