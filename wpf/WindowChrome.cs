using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ydp
{
    internal static class WindowChrome
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Paint the system title bar dark (DWMWA_USE_IMMERSIVE_DARK_MODE = 20).
        /// Without it Windows draws a white bar above our dark UI, which looks
        /// broken next to the HTML build's frameless dark window.
        /// </summary>
        public static void ApplyDarkTitleBar(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                int enabled = 1;
                // 20 on Windows 10 2004+, fall back to 19 on older builds.
                if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
                }
            }
            catch
            {
                // Cosmetic only — never let it stop the window from opening.
            }
        }
    }
}