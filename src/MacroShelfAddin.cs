using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
// No "using SolidWorks.Interop.swpublished" - ISwAddin is declared in
// InteropResolver.cs instead, so this type can be loaded with no SOLIDWORKS
// assembly present. See the comment there.

namespace MacroShelf
{
    [ComVisible(true)]
    [Guid(AddinGuid)]
    [ProgId("MacroShelf.Addin")]
    public partial class MacroShelfAddin : ISwAddin
    {
        // Changed for 0.8.0, and deliberately - CONVENTIONS and the handover both
        // said this must never move. The reason for changing it: sharing the
        // CLSID with the MacroDeck builds meant SOLIDWORKS saw the add-in as
        // renamed rather than replaced, so it kept the old tab and there was no
        // way to reach it. Three attempts failed - deleting the CommandManager
        // tab records, deleting the Custom API Toolbars entry, and calling
        // RemoveCommandGroup2 from the add-in, which reaches only groups created
        // in the current session and reported nothing to remove against intact
        // registrations.
        //
        // A new CLSID makes the uninstall of the old build remove its
        // registration outright, so SOLIDWORKS has no add-in behind the old tab.
        // The MSI UpgradeCode is untouched, so this still installs as an upgrade
        // rather than sitting alongside the previous version.
        public const string AddinGuid = "7B3E9A21-5C48-4D6F-9E82-3A1C7F5D0B64";
        internal const string AddinTitle = "MacroShelf";
        internal const string AddinDescription = "Turns a folder of macros into a SolidWorks toolbar";
        private const string TabName = "MacroShelf";

        // The version as shown to a person, read from AssemblyVersion so it can
        // never drift from the build.
        //
        // A release shows three parts (0.7.0); a test build shows four (0.7.0.2).
        // The fourth field distinguishes builds handed over for testing, and this
        // is the only place it is visible: an MSI ProductVersion carries just
        // three fields, so every test build of 0.7.0 looks identical in Add/Remove
        // Programs no matter how many times it is rebuilt.
        public static string VersionString()
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
        }

        // Group/flyout user IDs advance every rebuild because SolidWorks does
        // not reliably allow re-creating a group or flyout with an ID that was
        // removed earlier in the same session. Cross-session stability does
        // not matter: the tab's button boxes are refreshed in place from live
        // IDs on every build (runtime command IDs are assigned in creation
        // order, so IDs stored by a persisted tab go stale after any rescan).
        private const int MaxGenerations = 500;
        private int _generation;

        private ISldWorks _swApp;
        private ICommandManager _cmdMgr;
        private int _addinId;
        private int _iconGen = -1;

        private int _libraryFlyoutId;
        private int _groupId;
        private int _macroFlyoutBaseId;
        private bool _groupCreated;
        private List<int> _activeFlyoutIds = new List<int>();
        private List<MacroCommand> _runList = new List<MacroCommand>();
        private List<FlyoutDef> _flyoutDefs = new List<FlyoutDef>();
        private Timer _rebuildTimer;
        private Timer _restoreTabTimer;

        // A flyout's contents must be re-added inside its open callback
        // (see the "Create Flyouts in the CommandManager" SolidWorks API
        // example) - these defs remember what belongs in each flyout.
        private class FlyoutItemDef
        {
            public string Name;
            public string Hint;
            public int IconIndex;
            public string Callback;
        }

        private class FlyoutDef
        {
            public int UserId;
            public List<FlyoutItemDef> Items = new List<FlyoutItemDef>();

            // The Library flyout's items are not fixed: it grows an "Update
            // available" row once a check has found one. Since the open
            // callback repopulates every flyout anyway, that costs nothing -
            // and it is why the update item lives here rather than on the
            // toolbar, where making a button appear would force a rebuild and
            // leave another stale registration behind every session.
            public bool IsLibrary;
        }

        // Positions in the Library flyout's item icon strip, built in BuildUi.
        private const int IconSetup = 0;
        private const int IconScan = 1;
        private const int IconGuide = 2;
        private const int IconCheckForUpdates = 3;
        private const int IconUpdateAvailable = 4;

        private static readonly int[] DocTypes =
        {
            (int)swDocumentTypes_e.swDocPART,
            (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)swDocumentTypes_e.swDocDRAWING
        };

        // ----- ISwAddin -----

        public bool ConnectToSW(object thisSw, int cookie)
        {
            _swApp = (ISldWorks)thisSw;
            _addinId = cookie;
            _swApp.SetAddinCallbackInfo2(0, this, _addinId);
            _cmdMgr = _swApp.GetCommandManager(cookie);
            CleanIconCache();
            try
            {
                BuildUi();
            }
            catch (Exception ex)
            {
                Log("BuildUi failed during startup: " + ex);
                Warn("MacroShelf: building the toolbar failed: " + ex.Message +
                     "\r\nDetails were written to " + LogPath());
            }
            return true;
        }

        public bool DisconnectFromSW()
        {
            // Leave the command tab in place - SolidWorks persists it between
            // sessions, which keeps its position and avoids it stealing focus
            // as a "new" tab on every start.
            TearDownUi();
            if (_rebuildTimer != null)
            {
                _rebuildTimer.Dispose();
                _rebuildTimer = null;
            }
            if (_restoreTabTimer != null)
            {
                _restoreTabTimer.Dispose();
                _restoreTabTimer = null;
            }
            if (_cmdMgr != null)
            {
                Marshal.ReleaseComObject(_cmdMgr);
                _cmdMgr = null;
            }
            _swApp = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        // ----- COM registration (run by RegAsm / replicated by the MSI) -----

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string addinKey = "SOFTWARE\\SolidWorks\\Addins\\{" + t.GUID.ToString() + "}";
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(addinKey))
            {
                key.SetValue(null, 0);
                key.SetValue("Description", AddinDescription);
                key.SetValue("Title", AddinTitle);
            }
            string startupKey = "Software\\SolidWorks\\AddInsStartup\\{" + t.GUID.ToString() + "}";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(startupKey))
            {
                key.SetValue(null, 1, RegistryValueKind.DWord);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree("SOFTWARE\\SolidWorks\\Addins\\{" + t.GUID.ToString() + "}", false);
            }
            catch { }
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree("Software\\SolidWorks\\AddInsStartup\\{" + t.GUID.ToString() + "}", false);
            }
            catch { }
        }

        // ----- UI construction -----

        private void BuildUi()
        {
            _iconGen++;
            _runList = new List<MacroCommand>();
            _flyoutDefs = new List<FlyoutDef>();
            _activeFlyoutIds = new List<int>();
            _groupCreated = false;

            string iconDir = Path.Combine(AppDataDir(), "icons", "gen" + _iconGen);
            Directory.CreateDirectory(iconDir);

            SettingsData settings = Settings.Load();
            ScanResult scan = LibraryScanner.ScanAll(settings.Libraries);
            List<MacroButton> allButtons = LibraryScanner.ApplyPrefs(scan.Buttons, settings);
            Settings.Save(settings); // persists order slots assigned to newly found buttons

            // A button reaches the toolbar only if it, and its library, are
            // switched on and at least one of its macros is switched on.
            List<MacroButton> buttons = allButtons
                .Where(b => b.Enabled
                    && Settings.IsLibraryEnabled(settings, b.LibraryPath)
                    && b.EnabledMacros.Count > 0)
                .ToList();

            _generation = (_generation % MaxGenerations) + 1;
            _libraryFlyoutId = 5000 + _generation;
            _groupId = 8000 + _generation;
            _macroFlyoutBaseId = 20000 + (_generation * MaxFlyouts);

            // ----- "Library" flyout (Setup / Scan) -----
            List<Bitmap> libraryMainIcons = new List<Bitmap>();
            libraryMainIcons.Add(IconFactory.MakeLibraryIcon());
            string[] libraryMain = SaveStripsAndDispose(libraryMainIcons, iconDir, "library_main");

            // Every icon the flyout can ever show goes in the strip, because
            // the strip is fixed here at build time while the item list is
            // rebuilt on each open - see RefreshLibraryItems.
            List<Bitmap> libraryItemIcons = new List<Bitmap>();
            libraryItemIcons.Add(IconFactory.MakeSetupIcon());            // IconSetup
            libraryItemIcons.Add(IconFactory.MakeScanIcon());             // IconScan
            libraryItemIcons.Add(IconFactory.MakeGuideIcon());            // IconGuide
            libraryItemIcons.Add(IconFactory.MakeUpdateIcon());           // IconCheckForUpdates
            libraryItemIcons.Add(IconFactory.MakeUpdateAvailableIcon());  // IconUpdateAvailable
            string[] libraryItems = SaveStripsAndDispose(libraryItemIcons, iconDir, "library_items");

            FlyoutDef libraryDef = new FlyoutDef();
            libraryDef.UserId = _libraryFlyoutId;
            libraryDef.IsLibrary = true;
            RefreshLibraryItems(libraryDef);
            int libraryCmdId = CreateFlyout(libraryDef, "Library",
                "Set up or rescan the MacroShelf macro library", libraryMain, libraryItems);

            // Switching macros off can collapse a drop-down into a plain
            // button, so the split follows the enabled count, not the folder.
            List<MacroButton> multis = buttons.Where(b => b.EnabledMacros.Count > 1).ToList();

            // EVERY enabled macro gets a command in the one group, walked in
            // display order - so a drop-down's macros appear inline on the classic
            // toolbar, in the place the drop-down itself occupies on the tab.
            //
            // This works because the toolbar and the tab are filled by different
            // routes: SolidWorks generates the toolbar from the whole command
            // group, while the tab further down is populated explicitly with
            // chosen command IDs. So the tab keeps showing plain buttons and
            // drop-downs while the toolbar shows everything unfolded.
            //
            // It exists because a flyout CANNOT be placed on a classic toolbar -
            // the API help for CreateFlyoutGroup2 gives exactly two destinations,
            // a CommandManager tab and a context menu - so toolbar users would
            // otherwise never see the macros inside a drop-down at all.
            //
            // A drop-down's macros are registered twice, once here and once as
            // flyout items below, but both point at the same RunMacroN callback -
            // so no extra callback stubs, and MaxMacroCommands is unaffected.
            List<MacroButton> entryOwners = new List<MacroButton>();
            List<MacroCommand> entryMacros = new List<MacroCommand>();
            foreach (MacroButton b in buttons)
            {
                foreach (MacroCommand m in b.EnabledMacros)
                {
                    entryOwners.Add(b);
                    entryMacros.Add(m);
                }
            }

            // ONLY the first group of a session gets a classic toolbar.
            //
            // SolidWorks binds a classic toolbar to the command group that made it,
            // and a rebuild must make a new group (§7.3b). So every rebuild used to
            // add another "MacroShelf" row to Tools > Customize > Toolbars, and those
            // extra toolbars are traps: measured 2026-08-14, only the FIRST group of
            // a session survives a restart. _generation resets to 0 on load, so that
            // is always 8001 - a toolbar belonging to 8002 or later has no group in
            // the next session and is silently purged, taking the user's tick with it.
            //
            // Giving later groups no toolbar means no extra rows and no traps. The
            // session's one toolbar greys out when the macro set changes and comes
            // back correct - with the new set - after a restart. The CommandManager
            // tab is unaffected either way: it is refilled from live IDs every build.
            //
            // The title carries the generation as a fallback: if a row ever does
            // appear for a later group, it is identifiable rather than another
            // identical "MacroShelf".
            // Every group gets a toolbar. 0.7.0.7 tried giving rebuilt groups
            // HasToolbar = false, to stop Tools > Customize > Toolbars gaining a
            // row per rebuild - and it DID stop that, but it also dropped every
            // plain button from the CommandManager tab after any rebuild.
            //
            // A group with no toolbar does not hand out usable command IDs, so
            // get_CommandID gives the tab values that AddCommands silently ignores.
            // Drop-downs survived because a flyout does not depend on the group.
            //
            // The tab is the primary UI, so extra Customize rows are the lesser
            // evil. The generation in the title is what is left of the idea: the
            // durable toolbar is plain "MacroShelf" - only the first group of a
            // session survives a restart - and every later one is labelled
            // temporary, so the traps are at least visible.
            string groupTitle = (_generation == 1)
                ? AddinTitle
                : AddinTitle + " (temp" + _generation + " - do not use)";

            Dictionary<MacroButton, int> singleCmdIds = new Dictionary<MacroButton, int>();
            CommandGroup group = null;
            if (entryMacros.Count > 0)
            {
                int errors = 0;

                // The sixth argument is IgnorePreviousVersion. TRUE tells
                // SolidWorks to throw away everything it saved about this
                // command group - which includes where the user docked its
                // toolbar, so passing it unconditionally meant a toolbar
                // dragged elsewhere returned to the default on every restart.
                //
                // It cannot simply be FALSE either: reusing the saved layout
                // when the buttons have changed is what leaves a toolbar
                // showing macros that no longer exist. So it is asked only when
                // there is something to rebuild - when the set of enabled
                // macros differs from whatever the toolbar was last built from.
                // Only the FIRST group of a session has a toolbar SolidWorks
                // persists (§2a); later ones are the tempN groups, built at IDs
                // it has never seen, so there is no saved layout to keep or
                // discard and the flag is moot for them.
                //
                // That distinction is the whole of it. Recording the signature
                // on a mid-session rebuild describes a group whose layout is
                // never saved, while the layout that IS restored on the next
                // start belongs to the first group - built before the change.
                // Doing that left a dead button on the toolbar for a macro
                // switched off in the previous session.
                //
                // So the stored signature describes what the persisted toolbar
                // was last built from, and only the first group of a session
                // reads or writes it.
                bool ignorePrevious = true;
                if (_generation == 1)
                {
                    string signature = ToolbarSignature(groupTitle, entryMacros);
                    ignorePrevious = !string.Equals(
                        signature, settings.ToolbarSignature, StringComparison.Ordinal);
                    if (ignorePrevious)
                    {
                        settings.ToolbarSignature = signature;
                        try
                        {
                            Settings.Save(settings);
                        }
                        catch (Exception ex)
                        {
                            // Not fatal: the worst case is discarding the
                            // toolbar layout again next time.
                            Log("Could not save the toolbar signature: " + ex.Message);
                        }
                    }
                }

                group = _cmdMgr.CreateCommandGroup2(_groupId, groupTitle,
                    "MacroShelf macros", "MacroShelf macros", -1, ignorePrevious, ref errors);
                if (group == null)
                {
                    Log("CreateCommandGroup2 returned null (id " + _groupId + ", errors " + errors + ")");
                }
            }
            if (group != null)
            {
                group.HasToolbar = true;
                group.HasMenu = false;

                List<Bitmap> groupMainIcons = new List<Bitmap>();
                groupMainIcons.Add(IconFactory.MakeLibraryIcon());
                group.MainIconList = SaveStripsAndDispose(groupMainIcons, iconDir, "group_main");

                // Icon order must match the order items are added below, because an
                // item refers to its icon by index into this one strip.
                List<Bitmap> itemIcons = new List<Bitmap>();
                for (int i = 0; i < entryMacros.Count; i++)
                {
                    MacroButton owner = entryOwners[i];
                    // A folder's only enabled macro shows the folder's icon, since
                    // the folder is what that button represents. One of several
                    // shows its own.
                    itemIcons.Add(owner.EnabledMacros.Count == 1
                        ? LoadButtonIcon(owner)
                        : LoadItemIcon(owner, entryMacros[i]));
                }
                group.IconList = SaveStripsAndDispose(itemIcons, iconDir, "group_items");

                List<int> itemIndexes = new List<int>();
                List<MacroButton> itemOwners = new List<MacroButton>();
                for (int i = 0; i < entryMacros.Count; i++)
                {
                    MacroButton owner = entryOwners[i];
                    MacroCommand macro = entryMacros[i];
                    bool sole = owner.EnabledMacros.Count == 1;

                    int runIndex = AddRunCommand(macro);
                    if (runIndex < 0)
                    {
                        // Silent truncation used to be possible here: everything
                        // after this point gets no command item and then quietly
                        // never reaches the tab.
                        Log("Out of macro command slots (MaxMacroCommands = " +
                            MaxMacroCommands + ") at \"" + macro.DisplayName +
                            "\" in \"" + owner.Name + "\". That macro and the " +
                            (entryMacros.Count - i - 1) + " after it get no button.");
                        break;
                    }

                    // A sole macro is labelled with its folder name, matching the
                    // tab. One of several carries its own name, because on the
                    // toolbar it is a button in its own right.
                    string label = sole ? owner.Name : macro.DisplayName;
                    string hint = sole
                        ? FirstNonEmpty(macro.Description, owner.Description, "Run " + macro.DisplayName)
                        : FirstNonEmpty(macro.Description, "Run " + macro.DisplayName);

                    int itemIndex = group.AddCommandItem2(label, -1, hint,
                        label, i, "RunMacro" + runIndex, "EnableAlways", 100 + i,
                        (int)swCommandItemType_e.swToolbarItem);
                    itemIndexes.Add(itemIndex);
                    itemOwners.Add(owner);
                }
                group.Activate();
                _groupCreated = true;

                // Only a folder's sole enabled macro becomes a plain button on the
                // tab; the rest are reached through their drop-down there.
                for (int i = 0; i < itemIndexes.Count; i++)
                {
                    if (itemOwners[i].EnabledMacros.Count == 1)
                    {
                        singleCmdIds[itemOwners[i]] = group.get_CommandID(itemIndexes[i]);
                    }
                }
            }

            // Folders with several macros: one flyout (drop-down) per folder.
            Dictionary<MacroButton, int> flyoutCmdIds = new Dictionary<MacroButton, int>();
            for (int k = 0; k < multis.Count; k++)
            {
                if (_flyoutDefs.Count >= MaxFlyouts)
                {
                    break;
                }
                MacroButton b = multis[k];

                List<Bitmap> mainIcons = new List<Bitmap>();
                mainIcons.Add(LoadButtonIcon(b));
                string[] mainStrips = SaveStripsAndDispose(mainIcons, iconDir, "fly" + k + "_main");

                List<MacroCommand> entries = b.EnabledMacros;
                List<Bitmap> itemIcons = new List<Bitmap>();
                for (int m = 0; m < entries.Count; m++)
                {
                    itemIcons.Add(LoadItemIcon(b, entries[m]));
                }
                string[] itemStrips = SaveStripsAndDispose(itemIcons, iconDir, "fly" + k + "_items");

                FlyoutDef def = new FlyoutDef();
                def.UserId = _macroFlyoutBaseId + k;
                for (int m = 0; m < entries.Count; m++)
                {
                    int runIndex = AddRunCommand(entries[m]);
                    if (runIndex < 0)
                    {
                        break;
                    }
                    def.Items.Add(NewFlyoutItem(entries[m].DisplayName,
                        FirstNonEmpty(entries[m].Description, "Run " + entries[m].DisplayName),
                        m, "RunMacro" + runIndex));
                }
                int flyoutCmdId = CreateFlyout(def, b.Name,
                    FirstNonEmpty(b.Description, "Macros in " + b.Name), mainStrips, itemStrips);
                if (flyoutCmdId >= 0)
                {
                    flyoutCmdIds[b] = flyoutCmdId;
                }
            }

            // The tab itself, for part, assembly and drawing documents.
            int textBelow = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;
            int flyoutStyle = textBelow | (int)swCommandTabButtonFlyoutStyle_e.swCommandTabButton_ActionFlyout;

            List<int> buttonIds = new List<int>();
            List<int> buttonStyles = new List<int>();
            // Keyed off which lookup actually holds the button, not off IsMulti.
            // IsMulti counts every macro in the folder, where what belongs on the
            // tab depends on how many are switched ON: a folder of three with two
            // disabled is a plain button. Testing IsMulti dropped such a button
            // from the tab entirely - it is not in flyoutCmdIds because it is not a
            // drop-down, and the second branch never ran because IsMulti was true.
            List<string> dropped = new List<string>();
            foreach (MacroButton b in buttons)
            {
                int id;
                if (flyoutCmdIds.TryGetValue(b, out id))
                {
                    buttonIds.Add(id);
                    buttonStyles.Add(flyoutStyle);
                }
                else if (singleCmdIds.TryGetValue(b, out id))
                {
                    buttonIds.Add(id);
                    buttonStyles.Add(textBelow);
                }
                else
                {
                    // An enabled button that reached neither lookup never gets to
                    // the tab. That is always a fault, and it used to be silent -
                    // the button simply was not there, with nothing to say why.
                    dropped.Add(b.Name + " (" + b.EnabledMacros.Count + " enabled)");
                }
            }
            if (dropped.Count > 0)
            {
                Log("Buttons enabled but missing from the tab: " + string.Join(", ", dropped.ToArray()) +
                    ". Group created: " + _groupCreated + ", flyouts: " + flyoutCmdIds.Count +
                    ", plain: " + singleCmdIds.Count + " of " + buttons.Count + " buttons.");
            }
            else
            {
                // Recorded so a report of "buttons are missing" can be told apart
                // from the CommandManager simply clipping them - it does not scroll
                // (§7.8), so a narrow window hides buttons that were added fine.
                Log("Tab built: " + buttonIds.Count + " buttons (" + flyoutCmdIds.Count +
                    " drop-down, " + singleCmdIds.Count + " plain). None dropped.");
            }

            // The tab itself is never removed once created - removing it
            // makes SolidWorks jump to another tab (and auto-activate the
            // replacement as "new"). Its boxes are double-buffered: the new
            // buttons are added first and the stale boxes removed after, so
            // the tab is never empty and focus never leaves it.
            foreach (int docType in DocTypes)
            {
                CommandTab tab = _cmdMgr.GetCommandTab(docType, TabName);
                if (tab == null)
                {
                    tab = _cmdMgr.AddCommandTab(docType, TabName);
                    if (tab == null)
                    {
                        Log("AddCommandTab returned null for doc type " + docType);
                        continue;
                    }
                }
                object[] staleBoxes = null;
                try
                {
                    staleBoxes = tab.CommandTabBoxes() as object[];
                }
                catch { }

                if (libraryCmdId >= 0)
                {
                    CommandTabBox libraryBox = tab.AddCommandTabBox();
                    if (libraryBox != null)
                    {
                        libraryBox.AddCommands(new int[] { libraryCmdId }, new int[] { flyoutStyle });
                    }
                }
                if (buttonIds.Count > 0)
                {
                    CommandTabBox macroBox = tab.AddCommandTabBox();
                    if (macroBox != null)
                    {
                        macroBox.AddCommands(buttonIds.ToArray(), buttonStyles.ToArray());
                    }
                }

                RemoveTabBoxes(tab, staleBoxes);
            }
        }

        private void RemoveTabBoxes(CommandTab tab, object[] boxes)
        {
            if (boxes == null)
            {
                return;
            }
            foreach (object box in boxes)
            {
                try
                {
                    tab.RemoveCommandTabBox((CommandTabBox)box);
                }
                catch (Exception ex)
                {
                    Log("RemoveCommandTabBox failed: " + ex.Message);
                }
            }
        }

        // Rebuilt every time the Library flyout is opened, so the update row
        // can come and go without touching the command group.
        private static void RefreshLibraryItems(FlyoutDef def)
        {
            def.Items.Clear();
            def.Items.Add(NewFlyoutItem("Setup",
                "Manage your macro libraries, button order and visibility",
                IconSetup, "OnLibrarySetup"));
            def.Items.Add(NewFlyoutItem("Scan",
                "Rescan all macro libraries and refresh the toolbar",
                IconScan, "OnLibraryScan"));
            def.Items.Add(NewFlyoutItem("Guide",
                "How to structure your macro library",
                IconGuide, "OnLibraryGuide"));

            // Only ever shown off an answer already in hand. Opening a menu
            // must never reach the network.
            UpdateStatus known = UpdateChecker.Cached();
            if (known != null && known.Outcome == UpdateOutcome.UpdateAvailable)
            {
                def.Items.Add(NewFlyoutItem("Update available - " + known.LatestVersion,
                    "Open the MacroShelf releases page in your browser",
                    IconUpdateAvailable, "OnOpenReleases"));
            }

            def.Items.Add(NewFlyoutItem("Check for updates",
                "Ask GitHub whether a newer MacroShelf has been released",
                IconCheckForUpdates, "OnCheckForUpdates"));
        }

        // Identifies what the classic toolbar was built from, so a rebuild can
        // tell whether SolidWorks' saved layout is still valid.
        //
        // It covers everything that changes the toolbar's buttons: which macros
        // are enabled, the order they appear in, the label on each, and the
        // group's own title. It deliberately does NOT cover icons or hint text,
        // which can change without invalidating a layout keyed on commands.
        internal static string ToolbarSignature(string groupTitle, List<MacroCommand> macros)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(groupTitle).Append('\n');
            foreach (MacroCommand m in macros)
            {
                sb.Append(m.FolderPath == null ? "" : m.FolderPath.ToLowerInvariant());
                sb.Append('|');
                sb.Append(m.DisplayName).Append('\n');
            }
            using (System.Security.Cryptography.SHA256 sha =
                System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                StringBuilder hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    hex.Append(b.ToString("x2"));
                }
                return hex.ToString();
            }
        }

        private static FlyoutItemDef NewFlyoutItem(string name, string hint, int iconIndex, string callback)
        {
            FlyoutItemDef item = new FlyoutItemDef();
            item.Name = name;
            item.Hint = hint;
            item.IconIndex = iconIndex;
            item.Callback = callback;
            return item;
        }

        // Registers the flyout and its open callback ("FlyoutOpenN"), which
        // SolidWorks invokes on click and which must repopulate the items.
        // Returns the flyout's command ID, or -1 if it could not be created.
        private int CreateFlyout(FlyoutDef def, string title, string hint, string[] mainIcons, string[] itemIcons)
        {
            if (_flyoutDefs.Count >= MaxFlyouts)
            {
                return -1;
            }
            int defIndex = _flyoutDefs.Count;
            _flyoutDefs.Add(def);

            // Note: the ToolTip argument (3rd) is what the CommandManager
            // shows as the button label, so it must be the display name.
            FlyoutGroup flyout = _cmdMgr.CreateFlyoutGroup2(def.UserId, title, title, hint,
                mainIcons, itemIcons, "FlyoutOpen" + defIndex, "EnableAlways");
            if (flyout == null)
            {
                Log("CreateFlyoutGroup2 returned null (id " + def.UserId + ", title " + title + ")");
                return -1;
            }
            PopulateFlyout(flyout, def);
            _activeFlyoutIds.Add(def.UserId);
            return flyout.CmdID;
        }

        private static void PopulateFlyout(FlyoutGroup flyout, FlyoutDef def)
        {
            flyout.RemoveAllCommandItems();
            foreach (FlyoutItemDef item in def.Items)
            {
                flyout.AddCommandItem(item.Name, item.Hint, item.IconIndex, item.Callback, "EnableAlways");
            }
            flyout.FlyoutType = (int)swCommandFlyoutStyle_e.swCommandFlyoutStyle_Simple;
        }

        // Invoked (via the FlyoutOpenN stubs) every time a flyout is clicked.
        internal void FlyoutOpenAt(int index)
        {
            try
            {
                if (_cmdMgr == null || index < 0 || index >= _flyoutDefs.Count)
                {
                    return;
                }
                FlyoutDef def = _flyoutDefs[index];
                if (def.IsLibrary)
                {
                    RefreshLibraryItems(def);
                }
                FlyoutGroup flyout = _cmdMgr.GetFlyoutGroup(def.UserId);
                if (flyout != null)
                {
                    PopulateFlyout(flyout, def);
                }
            }
            catch (Exception ex)
            {
                Log("FlyoutOpenAt(" + index + ") failed: " + ex);
            }
        }

        // Called only from DisconnectFromSW - a mid-session rebuild removes the
        // stale generation directly.
        //
        // The command group is deliberately LEFT IN PLACE, for the same reason
        // the command tab above it is: SolidWorks writes its toolbar layout as
        // it closes, and it can only record the position of a toolbar that
        // still exists at that moment. Removing the group here meant there was
        // nothing left to save, so a toolbar the user had dragged somewhere
        // reappeared at the default on the next start no matter what the
        // creation flags said.
        //
        // The flyouts still go: they carry no persistent layout, and leaving
        // them registered serves nothing.
        private void TearDownUi()
        {
            RemoveCommands(0, false, _activeFlyoutIds);
            _activeFlyoutIds.Clear();
        }

        // RemoveCommandGroup2's second argument is RuntimeOnly. Passing TRUE removes
        // the command group but *keeps its toolbar registration in the registry*, so
        // every rebuild left one behind and Tools > Customize > Toolbars grew an extra
        // "MacroShelf" row each time a library was toggled or a scan run. Measured:
        // one new row per rebuild, collapsing back to one on restart.
        //
        // FALSE removes the toolbar information as well, which is what we want -
        // toolbar layout is deliberately disposable here, because the Library Manager
        // is the single source of truth for what appears (see README).
        //
        // Both calls report failure and neither used to be checked, so a failed
        // removal was silent. They are logged now: this is the one place that can
        // leak registrations, and a leak is invisible until someone opens Customize.
        private void RemoveCommands(int groupId, bool groupCreated, List<int> flyoutIds)
        {
            if (_cmdMgr == null)
            {
                return;
            }
            if (groupCreated)
            {
                try
                {
                    // RuntimeOnly = TRUE: keep the toolbar's registry entry, which
                    // is where SolidWorks stores where the user docked it. Passing
                    // false wipes that entry, so a toolbar dragged somewhere else
                    // came back at the default position on the next start.
                    //
                    // False was adopted to stop the duplicate Tools > Customize >
                    // Toolbars rows and, per §7.3b, had no effect on them - so it
                    // was costing a real behaviour and buying nothing.
                    int result = _cmdMgr.RemoveCommandGroup2(groupId, true);
                    if (result != (int)swRemoveCommandGroupErrors.swRemoveCommandGroup_Success)
                    {
                        Log("RemoveCommandGroup2(" + groupId + ") returned " + result +
                            " (expected " + (int)swRemoveCommandGroupErrors.swRemoveCommandGroup_Success +
                            " = success); a stale toolbar registration may remain");
                    }
                }
                catch (Exception ex)
                {
                    Log("RemoveCommandGroup2(" + groupId + ") threw: " + ex);
                }
            }
            foreach (int flyoutId in flyoutIds)
            {
                try
                {
                    if (!_cmdMgr.RemoveFlyoutGroup(flyoutId))
                    {
                        Log("RemoveFlyoutGroup(" + flyoutId + ") returned false");
                    }
                }
                catch (Exception ex)
                {
                    Log("RemoveFlyoutGroup(" + flyoutId + ") threw: " + ex);
                }
            }
        }

        // ----- Toolbar callbacks (invoked by name from SolidWorks) -----

        public int EnableAlways()
        {
            return 1;
        }

        public void OnLibrarySetup()
        {
            try
            {
                if (LibraryManagerForm.ShowManager())
                {
                    ScheduleRebuild();
                }
            }
            catch (Exception ex)
            {
                Log("Setup failed: " + ex);
                Warn("MacroShelf setup failed: " + ex.Message);
            }
        }

        public void OnLibraryGuide()
        {
            try
            {
                GuideForm.ShowGuide();
            }
            catch (Exception ex)
            {
                Log("Guide failed: " + ex);
                Warn("MacroShelf: could not open the guide: " + ex.Message);
            }
        }

        public void OnOpenReleases()
        {
            try
            {
                // The whole of the update feature: hand a URL to the browser.
                System.Diagnostics.Process.Start(UpdateChecker.ReleasesPageUrl);
            }
            catch (Exception ex)
            {
                Log("Opening the releases page failed: " + ex);
                Warn("MacroShelf: could not open your browser. The releases page is at\r\n"
                    + UpdateChecker.ReleasesPageUrl);
            }
        }

        public void OnCheckForUpdates()
        {
            try
            {
                StartUpdateCheck();
            }
            catch (Exception ex)
            {
                Log("Update check failed to start: " + ex);
                Warn("MacroShelf: could not check for updates: " + ex.Message);
            }
        }

        public void OnLibraryScan()
        {
            SettingsData settings = Settings.Load();
            if (settings.Libraries.Count == 0)
            {
                Warn("MacroShelf: no macro libraries have been added yet. Use Library > Setup first.");
                return;
            }
            ScheduleRebuild();
        }

        // ----- checking for updates -----
        //
        // The request runs on a worker thread so SolidWorks never freezes on a
        // slow or unreachable network. Getting back is the awkward half: there
        // is no SynchronizationContext to post to inside SolidWorks, and
        // putting a dialog up from the worker would leave a window owned by a
        // thread that owns nothing else. So a WinForms timer - which ticks on
        // the thread that created it, the same trick ScheduleRebuild uses -
        // watches for the answer and shows it from the right thread.
        private Timer _updateTimer;
        private volatile bool _updateRunning;
        private UpdateStatus _updateResult;
        private int _updateWaitedMs;

        // Comfortably past the request's own timeout; a safety net, not a
        // deadline anything should reach.
        private const int UpdateWaitLimitMs = 30000;

        private void StartUpdateCheck()
        {
            if (_updateRunning)
            {
                return; // already in flight; the timer is still watching
            }

            UpdateStatus known = UpdateChecker.Cached();
            if (known != null)
            {
                ShowUpdateResult(known);
                return;
            }

            string installed = VersionString();
            _updateResult = null;
            _updateRunning = true;

            // Fully qualified: importing System.Threading would make every
            // bare "Timer" in this file ambiguous, and every one of them has
            // to stay the WinForms timer that ticks on the UI thread.
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                UpdateStatus status;
                try
                {
                    status = UpdateChecker.Check(installed);
                }
                catch (Exception ex)
                {
                    status = UpdateStatus.Failure(ex.Message);
                }
                _updateResult = status;
                // A volatile write, so the timer thread cannot see this flag
                // clear before it can see the result it is guarding.
                _updateRunning = false;
            });
            worker.IsBackground = true;
            worker.Name = "MacroShelf update check";
            worker.Start();

            if (_updateTimer == null)
            {
                _updateTimer = new Timer();
                _updateTimer.Interval = 150;
                _updateTimer.Tick += OnUpdateTimerTick;
            }
            _updateWaitedMs = 0;
            _updateTimer.Start();
        }

        private void OnUpdateTimerTick(object sender, EventArgs e)
        {
            _updateWaitedMs += _updateTimer.Interval;
            if (_updateRunning && _updateWaitedMs < UpdateWaitLimitMs)
            {
                return;
            }
            _updateTimer.Stop();
            UpdateStatus status = _updateResult;
            _updateResult = null;
            if (status == null)
            {
                status = UpdateStatus.Failure(
                    "The check is taking longer than expected. Try again in a moment.");
            }
            ShowUpdateResult(status);
        }

        private void ShowUpdateResult(UpdateStatus status)
        {
            string installed = VersionString();
            switch (status.Outcome)
            {
                case UpdateOutcome.UpdateAvailable:
                    // "Update available" also appears in the Library flyout from
                    // now on, for as long as the answer stays fresh.
                    if (Ask("MacroShelf " + status.LatestVersion + " is available."
                        + "\r\n\r\nYou have " + installed + "."
                        + "\r\n\r\nOpen the releases page in your browser?"))
                    {
                        OnOpenReleases();
                    }
                    break;

                case UpdateOutcome.UpToDate:
                    Inform("MacroShelf " + installed + " is up to date.");
                    break;

                case UpdateOutcome.NoReleases:
                    Inform("No MacroShelf releases have been published yet."
                        + "\r\n\r\nYou have " + installed + ".");
                    break;

                default:
                    Log("Update check failed: " + status.Detail);
                    Warn("Couldn't reach GitHub. Check your connection, or visit the "
                        + "releases page:\r\n" + UpdateChecker.ReleasesPageUrl
                        + "\r\n\r\n" + status.Detail);
                    break;
            }
        }

        // Rebuilding tears down the very command group whose callback we are inside,
        // so defer it until SolidWorks is back in its message loop.
        private void ScheduleRebuild()
        {
            if (_rebuildTimer == null)
            {
                _rebuildTimer = new Timer();
                _rebuildTimer.Interval = 150;
                _rebuildTimer.Tick += OnRebuildTimerTick;
            }
            _rebuildTimer.Start();
        }

        private void OnRebuildTimerTick(object sender, EventArgs e)
        {
            _rebuildTimer.Stop();
            try
            {
                // Double-buffer the rebuild: create the new generation of
                // commands and swap the tab boxes first, and only then remove
                // the old generation - the tab never goes empty, so focus
                // should never leave it. The restore below is a safety net.
                bool restoreTab = IsMacroShelfTabActive();
                int staleGroupId = _groupId;
                bool staleGroupCreated = _groupCreated;
                List<int> staleFlyoutIds = _activeFlyoutIds;
                BuildUi();
                RemoveCommands(staleGroupId, staleGroupCreated, staleFlyoutIds);
                if (restoreTab)
                {
                    ScheduleTabRestore();
                }
            }
            catch (Exception ex)
            {
                Log("Rebuild failed: " + ex);
                Warn("MacroShelf: rebuilding the toolbar failed: " + ex.Message +
                     "\r\nDetails were written to " + LogPath());
            }
        }

        private int ActiveDocType()
        {
            try
            {
                ModelDoc2 doc = _swApp.ActiveDoc as ModelDoc2;
                if (doc == null)
                {
                    return -1;
                }
                return doc.GetType();
            }
            catch
            {
                return -1;
            }
        }

        private bool IsMacroShelfTabActive()
        {
            try
            {
                int docType = ActiveDocType();
                if (docType < 0)
                {
                    return false;
                }
                CommandTab tab = _cmdMgr.GetCommandTab(docType, TabName);
                return tab != null && tab.Active;
            }
            catch
            {
                return false;
            }
        }

        // Deferred so the freshly added tab is fully registered before we
        // activate it.
        private void ScheduleTabRestore()
        {
            if (_restoreTabTimer == null)
            {
                _restoreTabTimer = new Timer();
                _restoreTabTimer.Interval = 120;
                _restoreTabTimer.Tick += OnRestoreTabTick;
            }
            _restoreTabTimer.Start();
        }

        private void OnRestoreTabTick(object sender, EventArgs e)
        {
            _restoreTabTimer.Stop();
            try
            {
                int docType = ActiveDocType();
                if (docType < 0)
                {
                    return;
                }
                CommandTab tab = _cmdMgr.GetCommandTab(docType, TabName);
                if (tab != null)
                {
                    tab.Active = true;
                }
            }
            catch (Exception ex)
            {
                Log("Restoring the active tab failed: " + ex);
            }
        }

        // ----- Running macros -----

        internal void RunMacroAt(int index)
        {
            if (_swApp == null || index < 0 || index >= _runList.Count)
            {
                return;
            }
            MacroCommand cmd = _runList[index];
            try
            {
                if (!File.Exists(cmd.MacroPath))
                {
                    Warn("MacroShelf: macro file not found:\r\n" + cmd.MacroPath +
                         "\r\n\r\nUse Library > Scan to refresh the toolbar.");
                    return;
                }
                string moduleName;
                string procName;
                FindEntryPoint(cmd.MacroPath, out moduleName, out procName);
                int error = 0;
                bool ok = _swApp.RunMacro2(cmd.MacroPath, moduleName, procName,
                    (int)swRunMacroOption_e.swRunMacroUnloadAfterRun, out error);
                if (!ok && !(moduleName == "" && procName == "main"))
                {
                    ok = _swApp.RunMacro2(cmd.MacroPath, "", "main",
                        (int)swRunMacroOption_e.swRunMacroUnloadAfterRun, out error);
                }
                if (!ok)
                {
                    Warn("MacroShelf: failed to run macro:\r\n" + cmd.MacroPath +
                         "\r\n(error code " + error + ")");
                }
            }
            catch (Exception ex)
            {
                Log("RunMacroAt(" + index + ") failed for " + cmd.MacroPath + ": " + ex);
                Warn("MacroShelf: failed to run macro:\r\n" + cmd.MacroPath + "\r\n" + ex.Message);
            }
        }

        private void FindEntryPoint(string macroPath, out string moduleName, out string procName)
        {
            moduleName = "";
            procName = "";
            try
            {
                object methodsObj = _swApp.GetMacroMethods(macroPath,
                    (int)swMacroMethods_e.swMethodsWithoutArguments);
                string[] methods = methodsObj as string[];
                if (methods == null)
                {
                    object[] raw = methodsObj as object[];
                    if (raw != null)
                    {
                        methods = raw.Select(o => Convert.ToString(o)).ToArray();
                    }
                }
                if (methods == null)
                {
                    return;
                }
                for (int i = 0; i < methods.Length; i++)
                {
                    string entry = methods[i];
                    if (string.IsNullOrEmpty(entry))
                    {
                        continue;
                    }
                    int dot = entry.IndexOf('.');
                    if (dot <= 0 || dot >= entry.Length - 1)
                    {
                        continue;
                    }
                    string module = entry.Substring(0, dot);
                    string proc = entry.Substring(dot + 1);
                    // Default to the first entry point; prefer one called "main".
                    if (moduleName.Length == 0 || string.Equals(proc, "main", StringComparison.OrdinalIgnoreCase))
                    {
                        moduleName = module;
                        procName = proc;
                    }
                }
            }
            catch { }
        }

        // ----- Helpers -----

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return "";
        }

        private int AddRunCommand(MacroCommand cmd)
        {
            if (_runList.Count >= MaxMacroCommands)
            {
                return -1;
            }
            _runList.Add(cmd);
            return _runList.Count - 1;
        }

        private static Bitmap LoadButtonIcon(MacroButton button)
        {
            // When the button resolves to a single macro, that macro folder's
            // own icon is the more specific choice.
            List<MacroCommand> enabled = button.EnabledMacros;
            if (enabled.Count == 1 && !string.IsNullOrEmpty(enabled[0].IconPath))
            {
                Bitmap own = IconFactory.LoadUserIcon(enabled[0].IconPath);
                if (own != null)
                {
                    return own;
                }
            }
            if (!string.IsNullOrEmpty(button.IconPath))
            {
                Bitmap icon = IconFactory.LoadUserIcon(button.IconPath);
                if (icon != null)
                {
                    return icon;
                }
            }
            return IconFactory.MakeTileIcon(button.Name);
        }

        // Icon for one entry in a drop-down: its own sidecar image if present,
        // otherwise the button's icon.
        private static Bitmap LoadItemIcon(MacroButton button, MacroCommand macro)
        {
            if (!string.IsNullOrEmpty(macro.IconPath))
            {
                Bitmap icon = IconFactory.LoadUserIcon(macro.IconPath);
                if (icon != null)
                {
                    return icon;
                }
            }
            return LoadButtonIcon(button);
        }

        private static string[] SaveStripsAndDispose(List<Bitmap> icons, string dir, string baseName)
        {
            try
            {
                return IconFactory.SaveStrips(icons, dir, baseName);
            }
            finally
            {
                foreach (Bitmap b in icons)
                {
                    if (b != null)
                    {
                        b.Dispose();
                    }
                }
            }
        }

        private void Warn(string message)
        {
            try
            {
                if (_swApp != null)
                {
                    _swApp.SendMsgToUser2(message,
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                }
            }
            catch { }
        }

        private void Inform(string message)
        {
            try
            {
                if (_swApp != null)
                {
                    _swApp.SendMsgToUser2(message,
                        (int)swMessageBoxIcon_e.swMbInformation,
                        (int)swMessageBoxBtn_e.swMbOk);
                }
            }
            catch { }
        }

        // SolidWorks' own dialog rather than a WinForms MessageBox, so it is
        // owned and positioned like every other prompt the user sees.
        private bool Ask(string question)
        {
            try
            {
                if (_swApp == null)
                {
                    return false;
                }
                int answer = _swApp.SendMsgToUser2(question,
                    (int)swMessageBoxIcon_e.swMbQuestion,
                    (int)swMessageBoxBtn_e.swMbYesNo);
                return answer == (int)swMessageBoxResult_e.swMbHitYes;
            }
            catch
            {
                return false;
            }
        }

        private static string AppDataDir()
        {
            string dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "MacroShelf");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string LogPath()
        {
            return Path.Combine(AppDataDir(), "macroshelf.log");
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath(),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
            }
            catch { }
        }

        private static void CleanIconCache()
        {
            try
            {
                string icons = Path.Combine(AppDataDir(), "icons");
                if (Directory.Exists(icons))
                {
                    Directory.Delete(icons, true);
                }
            }
            catch { }
        }
    }
}
