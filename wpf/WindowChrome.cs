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
        /// Paint the system title bar dark. Without it Windows draws a white bar
        /// above our dark UI, which looks broken next to the HTML build's frameless
        /// window.
        ///
        /// Two independent mechanisms are used because neither works everywhere:
        /// the immersive dark-mode flag is ignored when the user's own app theme
        /// is light, and the caption/text colour attributes (Win10 2004+) are the
        /// only thing that reliably matches our background. Both are applied, each
        /// in its own try/catch, and a failure of any of them is cosmetic only.
        /// </summary>
        public static void ApplyDarkTitleBar(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                // Immersive dark mode: 19 on Win10 1809, 20 on 2004+. Both are
                // attempted unconditionally - on some builds the newer attribute
                // reports success while having no visible effect.
                var on = 1;
                Try(handle, 19, ref on);
                Try(handle, 20, ref on);

                // Explicit colours make the bar dark even with a light system theme.
                var caption = 0x001F1814;   // COLORREF is 0x00BBGGRR: our #14181F
                var text = 0x00F2EDE6;      // #E6EDF2, the same near-white as the UI
                Try(handle, 35, ref caption);
                Try(handle, 36, ref text);
            }
            catch
            {
                // Cosmetic only — never let it stop the window from opening.
            }
        }

        private static void Try(IntPtr handle, int attribute, ref int value)
        {
            try { DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)); }
            catch { /* attribute unsupported on this build */ }
        }
    }
}