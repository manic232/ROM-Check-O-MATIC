// ROM Check O-MATIC - choosing a folder with the modern Windows picker (the one File Explorer uses), which has an
// address bar, Quick Access and dark mode. The old tree-only folder box is used only if it can't be shown.

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RomCheckOMatic
{
    static class FolderPicker
    {
        // Returns the chosen folder, or null if cancelled. start: the folder it opens in, if it exists.
        public static string Choose(IWin32Window owner, string title, string start)
        {
            try { return ChooseModern(owner, title, start); }
            catch (COMException) { }
            catch (InvalidCastException) { }
            using (var d = new FolderBrowserDialog { Description = title, ShowNewFolderButton = true })
            {
                if (!string.IsNullOrEmpty(start) && System.IO.Directory.Exists(start)) d.SelectedPath = start;
                return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
            }
        }

        static string ChooseModern(IWin32Window owner, string title, string start)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogClass();
            try
            {
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(start) && System.IO.Directory.Exists(start))
                {
                    IShellItem folder;
                    var iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out folder) == 0 && folder != null)
                    {
                        dialog.SetFolder(folder);
                        Marshal.ReleaseComObject(folder);
                    }
                }
                int hr = dialog.Show(owner == null ? IntPtr.Zero : owner.Handle);
                if (hr == ERROR_CANCELLED) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                IShellItem result;
                dialog.GetResult(out result);
                try
                {
                    IntPtr name;
                    result.GetDisplayName(SIGDN_FILESYSPATH, out name);
                    try { return Marshal.PtrToStringUni(name); }
                    finally { Marshal.FreeCoTaskMem(name); }
                }
                finally { Marshal.ReleaseComObject(result); }
            }
            finally { Marshal.ReleaseComObject(dialog); }
        }

        const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogClass { }

        // IFileOpenDialog, as far as GetResult (the methods after it aren't needed). The order must match Windows'.
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint type, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
