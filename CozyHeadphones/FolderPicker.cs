using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace CozyHeadphones
{
    [SupportedOSPlatform("windows")]
    public static class FolderPicker
    {
        private const uint PickFolders = 0x20;
        private const uint FileSystemPath = 0x80058000;
        private const int Cancelled = unchecked((int)0x800704C7);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog
        {
        }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig]
            int Show(IntPtr owner);

            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IntPtr folder);
            void SetFolder(IntPtr folder);
            void GetFolder(out IntPtr folder);
            void GetCurrentSelection(out IntPtr item);

            void SetFileName(
                [MarshalAs(UnmanagedType.LPWStr)] string name);

            void GetFileName(out IntPtr name);

            void SetTitle(
                [MarshalAs(UnmanagedType.LPWStr)] string title);

            void SetOkButtonLabel(
                [MarshalAs(UnmanagedType.LPWStr)] string text);

            void SetFileNameLabel(
                [MarshalAs(UnmanagedType.LPWStr)] string label);

            void GetResult(out IShellItem item);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(
                IntPtr bindContext,
                ref Guid handler,
                ref Guid iid,
                out IntPtr result);

            void GetParent(out IShellItem parent);

            void GetDisplayName(
                uint kind,
                out IntPtr name);
        }

        public static Task<string?> ChooseFolderAsync(
            IntPtr ownerHandle)
        {
            var completion = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Thread thread = new(() =>
            {
                try
                {
                    string? result = ShowFolderDialog(ownerHandle);
                    completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            return completion.Task;
        }

        private static string? ShowFolderDialog(
            IntPtr ownerHandle)
        {
            IFileOpenDialog? dialog = null;
            IShellItem? item = null;
            IntPtr pathPointer = IntPtr.Zero;

            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialog();

                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | PickFolders);
                dialog.SetTitle("Elegí tu carpeta de música");

                // Se muestra con la ventana del juego como dueña y, si Windows
                // la rechaza (handle inválido tras cambiar de modo de pantalla),
                // se reintenta sin dueña en vez de fallar en silencio.
                int result = dialog.Show(ownerHandle);

                if (result != 0 && result != Cancelled)
                    result = dialog.Show(IntPtr.Zero);

                // El usuario canceló el selector.
                if (result == Cancelled)
                    return null;

                if (result != 0)
                    Marshal.ThrowExceptionForHR(result);

                dialog.GetResult(out item);
                item.GetDisplayName(
                    FileSystemPath,
                    out pathPointer);

                return Marshal.PtrToStringUni(pathPointer);
            }
            finally
            {
                if (pathPointer != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pathPointer);

                if (item != null)
                    Marshal.ReleaseComObject(item);

                if (dialog != null)
                    Marshal.ReleaseComObject(dialog);
            }
        }
    }
}