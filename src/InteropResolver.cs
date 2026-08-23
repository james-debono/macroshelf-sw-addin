using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MacroShelf
{
    // SOLIDWORKS' add-in interface, declared here rather than taken from
    // SolidWorks.Interop.swpublished.dll.
    //
    // This is what lets MacroShelf load with no SOLIDWORKS assembly present.
    // The CLR must load every interface a type implements *before* any of that
    // type's code runs, so a class implementing the real ISwAddin cannot be
    // created at all unless swpublished.dll is already reachable - and no
    // resolver of ours could be installed in time. Declaring the interface here
    // breaks that dependency: the signatures use only `object` and `int`, so
    // nothing SOLIDWORKS-specific is needed to load the type, and everything
    // else can be resolved later from the user's own installation.
    //
    // The IID and both signatures are as published in the SOLIDWORKS API
    // documentation for ISwAddin, and must match exactly - a COM interface is
    // called by vtable slot, so a wrong signature is not a compile error, it is
    // a corrupted stack at run time.
    [ComImport]
    [Guid("DA306A0D-EAC5-4406-8610-B1DA805D9270")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISwAddin
    {
        bool ConnectToSW([In, MarshalAs(UnmanagedType.IDispatch)] object ThisSW, [In] int Cookie);
        bool DisconnectFromSW();
    }

    // Finds the SOLIDWORKS API assemblies on the machine that is running them,
    // instead of MacroShelf carrying its own copies.
    //
    // It has to go through AssemblyResolve rather than a copy alongside the
    // add-in, because the assemblies are strong-named and their version moves
    // with each SOLIDWORKS release - 30.5.0.49 for 2022, 32.5.0.48 for 2024,
    // 33.5.0.53 for 2025. MacroShelf is compiled against the oldest so that one
    // build serves all three, and ordinary strong-name binding is an
    // exact-version match, so a fixed reference to one version would simply
    // fail to bind against another. AssemblyResolve is the documented escape
    // hatch: whatever it returns is accepted, version differences and all. That
    // is exactly what is wanted here, because the COM interfaces underneath are
    // stable.
    internal static class InteropResolver
    {
        private const string RedistSubPath = @"api\redist";
        private static readonly object Gate = new object();
        private static bool _installed;
        private static string _folder;

        // Called from MacroShelfAddin's static constructor, which the CLR runs
        // before the add-in object is created and therefore before any method
        // body that mentions a SOLIDWORKS type is compiled.
        public static void Install()
        {
            lock (Gate)
            {
                if (_installed)
                {
                    return;
                }
                _installed = true;
            }
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name;
                if (simpleName == null ||
                    !simpleName.StartsWith("SolidWorks.Interop.", StringComparison.OrdinalIgnoreCase))
                {
                    return null; // not ours to answer
                }
                string folder = RedistFolder();
                if (folder == null)
                {
                    Note("could not locate " + RedistSubPath + " for " + simpleName);
                    return null;
                }
                string path = Path.Combine(folder, simpleName + ".dll");
                if (!File.Exists(path))
                {
                    Note("no " + simpleName + ".dll in " + folder);
                    return null;
                }
                return Assembly.LoadFrom(path);
            }
            catch (Exception ex)
            {
                Note("resolving " + args.Name + " failed: " + ex.Message);
                return null;
            }
        }

        // The add-in runs inside SLDWORKS.exe, so the running application's own
        // folder is the authoritative answer and needs no registry lookup. The
        // rest are fallbacks, ending with MacroShelf's own folder so that an
        // installation which still has the assemblies beside it keeps working.
        internal static string RedistFolder()
        {
            if (_folder != null)
            {
                return _folder;
            }
            foreach (string candidate in CandidateFolders())
            {
                if (candidate == null)
                {
                    continue;
                }
                try
                {
                    string redist = Path.Combine(candidate, RedistSubPath);
                    if (File.Exists(Path.Combine(redist, "SolidWorks.Interop.sldworks.dll")))
                    {
                        _folder = redist;
                        return _folder;
                    }
                    // A folder that already *is* the redist folder.
                    if (File.Exists(Path.Combine(candidate, "SolidWorks.Interop.sldworks.dll")))
                    {
                        _folder = candidate;
                        return _folder;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string[] CandidateFolders()
        {
            string host = null;
            try
            {
                host = Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            }
            catch { }

            string beside = null;
            try
            {
                beside = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch { }

            return new string[]
            {
                host,                                     // SLDWORKS.exe's own folder
                AppDomain.CurrentDomain.BaseDirectory,    // same thing when hosted
                FromRegistry(),
                beside                                    // an older install that still bundles them
            };
        }

        // Last resort, for the case where the add-in is loaded by something
        // other than SOLIDWORKS itself. Walks HKLM\SOFTWARE\SolidWorks looking
        // for an install location, newest first.
        private static string FromRegistry()
        {
            try
            {
                using (RegistryKey root = RegistryKey
                    .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\SolidWorks"))
                {
                    if (root == null)
                    {
                        return null;
                    }
                    string[] names = root.GetSubKeyNames();
                    Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                    Array.Reverse(names); // newest release first
                    foreach (string name in names)
                    {
                        using (RegistryKey setup = root.OpenSubKey(name + @"\Setup"))
                        {
                            if (setup == null)
                            {
                                continue;
                            }
                            string path = setup.GetValue("SolidWorks Folder") as string;
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                            {
                                return path;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // Failing to resolve these is fatal to the add-in and invisible
        // otherwise, so it is worth a line in the log even though nothing here
        // can show a dialog - at this point the UI does not exist yet.
        private static void Note(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroShelf");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "macroshelf.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "  interop resolver: " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    // The static constructor lives with the resolver so the ordering is obvious:
    // the CLR runs it before the add-in object is created, and therefore before
    // any method body mentioning a SOLIDWORKS type is compiled.
    public partial class MacroShelfAddin
    {
        static MacroShelfAddin()
        {
            InteropResolver.Install();
        }
    }
}
