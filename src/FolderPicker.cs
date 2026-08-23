using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MacroShelf
{
    // Modern Vista-style folder picker (the same dialog SolidWorks uses for
    // File > Open), reached via the IFileOpenDialog COM API because WinForms
    // on .NET Framework only offers the old tree-style FolderBrowserDialog.
    // Falls back to the legacy dialog if the COM call fails for any reason.
    internal static class FolderPicker
    {
        public static string Show(string initialPath, string title)
        {
            try
            {
                return ShowModernDialog(initialPath, title);
            }
            catch
            {
                return ShowLegacyDialog(initialPath, title);
            }
        }

        private static string ShowModernDialog(string initialPath, string title)
        {
            IFileDialog dialog = (IFileDialog)new FileOpenDialogRCW();
            uint options;
            dialog.GetOptions(out options);
            options |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR;
            dialog.SetOptions(options);
            dialog.SetTitle(title);

            if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
            {
                Guid shellItemGuid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                try
                {
                    IShellItem initial = SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref shellItemGuid);
                    dialog.SetFolder(initial);
                }
                catch { }
            }

            // The add-in runs inside SolidWorks, so the process main window is
            // the SolidWorks frame - use it as owner so the dialog is modal.
            IntPtr owner = IntPtr.Zero;
            try
            {
                owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            }
            catch { }

            uint hr = dialog.Show(owner);
            if (hr != 0)
            {
                return null; // cancelled (or failed - either way, no selection)
            }

            IShellItem result;
            dialog.GetResult(out result);
            IntPtr pathPtr = IntPtr.Zero;
            try
            {
                result.GetDisplayName(SIGDN_FILESYSPATH, out pathPtr);
                return Marshal.PtrToStringUni(pathPtr);
            }
            finally
            {
                if (pathPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
        }

        private static string ShowLegacyDialog(string initialPath, string title)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = title;
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                {
                    dialog.SelectedPath = initialPath;
                }
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.SelectedPath;
                }
                return null;
            }
        }

        private const uint FOS_NOCHANGEDIR = 0x00000008;
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern IShellItem SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW
        {
        }

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig]
            uint Show(IntPtr hwndParent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName(out IntPtr pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, uint fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
