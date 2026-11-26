using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using Accessibility;

namespace XunXun;

static class DesktopIcons
{
    private const uint ClientObject = unchecked((uint)-4);
    private const int ListItemRole = 0x22;
    private const int HiddenStates = 0x18000; // Invisible or off screen.

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr previous, string type, string? title);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    public static Rect[] Read()
    {
        var icons = new List<Rect>();
        foreach (string host in new[] { "Progman", "WorkerW" })
        {
            var window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, host, null)) != IntPtr.Zero)
            {
                var view = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view == IntPtr.Zero) continue;
                var list = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
                if (list == IntPtr.Zero || !WindowsNative.IsWindowVisible(list)) continue;
                ReadList(list, icons);
            }
        }
        return icons.ToArray();
    }

    private static void ReadList(IntPtr list, List<Rect> icons)
    {
        try
        {
            var iid = typeof(IAccessible).GUID;
            if (AccessibleObjectFromWindow(list, ClientObject, ref iid, out var accessible) != 0
                || accessible is null) return;
            int count = accessible.accChildCount;
            for (int child = 1; child <= count; child++)
            {
                try
                {
                    if (accessible.get_accRole(child) is not int role || role != ListItemRole
                        || accessible.get_accState(child) is int state && (state & HiddenStates) != 0)
                        continue;
                    accessible.accLocation(out int x, out int y, out int width, out int height, child);
                    if (width > 0 && height > 0) icons.Add(new Rect(x, y, width, height));
                }
                // Explorer's count can include a child object without a numeric child ID.
                catch (Exception error) when (error is COMException or ArgumentException) { }
            }
        }
        catch (COMException) { } // Explorer can restart while the pet is running.
    }
}
