using System;
using System.Runtime.InteropServices;
using System.Text;
using Cthangover.Core.Utils;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Windows clipboard contention is a normal failed operation, not a native engine error. One open/close owns
    /// each transfer; the OS owns successful text allocations.
    /// </summary>
    public static class PerfOverlayClipboard
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(nint owner);
        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        private static extern nint GetActiveWindow();
        [DllImport("user32.dll")]
        private static extern nint SetClipboardData(uint format, nint memory);
        [DllImport("user32.dll")]
        private static extern nint GetClipboardData(uint format);
        [DllImport("kernel32.dll")]
        private static extern nint GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")]
        private static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(nint memory);
        [DllImport("kernel32.dll")]
        private static extern nint GlobalFree(nint memory);

        /// <summary>
        /// Borrows the platform clipboard for one read and returns false outside runtime or during contention. An
        /// absent Unicode payload is an empty string; every successful Windows open and lock is released.
        /// </summary>
        public static bool TryGet(out string text)
        {
            text = null;
            if (!GameEnvironment.IsRuntime)
            {
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                text = DisplayServer.ClipboardGet();
                return true;
            }

            if (!OpenClipboard(0))
            {
                return false;
            }

            try
            {
                var handle = GetClipboardData(13);
                if (handle == 0)
                {
                    text = "";
                    return true;
                }

                var pointer = GlobalLock(handle);
                if (pointer == 0)
                {
                    return false;
                }

                try
                {
                    text = Marshal.PtrToStringUni(pointer) ?? "";
                    return true;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        /// <summary>
        /// Copies text into movable Windows memory, transferring allocation ownership only after a successful
        /// clipboard set. Failures free the allocation; other runtime platforms use DisplayServer.
        /// </summary>
        public static bool TrySet(string text)
        {
            if (!GameEnvironment.IsRuntime || text == null)
            {
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                DisplayServer.ClipboardSet(text);
                return true;
            }

            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var handle = GlobalAlloc(2, (nuint)bytes.Length);
            if (handle == 0)
            {
                return false;
            }

            try
            {
                var pointer = GlobalLock(handle);
                if (pointer == 0)
                {
                    return false;
                }

                try
                {
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                var owner = GetActiveWindow();
                if (owner == 0 || !OpenClipboard(owner))
                {
                    return false;
                }

                try
                {
                    if (!EmptyClipboard() || SetClipboardData(13, handle) == 0)
                    {
                        return false;
                    }

                    handle = 0;
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            finally
            {
                if (handle != 0)
                {
                    GlobalFree(handle);
                }
            }
        }
    }
}
