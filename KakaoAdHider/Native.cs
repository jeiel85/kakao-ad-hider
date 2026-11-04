using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KakaoAdHider
{
    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        public const uint GW_HWNDNEXT = 2, GW_OWNER = 4, GW_CHILD = 5;
        public const int GWL_STYLE = -16;
        public const int WS_VISIBLE = 0x10000000;
        public const int SW_HIDE = 0, SW_SHOWNA = 8;
        public const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_ASYNCWINDOWPOS = 0x4000;
        public const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const int OBJID_WINDOW = 0, CHILDID_SELF = 0;

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr hWnd, IntPtr region);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr result, IntPtr first, IntPtr second, int mode);
        [DllImport("gdi32.dll")] public static extern bool EqualRgn(IntPtr first, IntPtr second);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr handle);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT rect, int points);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        public static string ClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string Text(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool HasVisibleStyle(IntPtr hWnd) => (GetWindowLong(hWnd, GWL_STYLE) & WS_VISIBLE) != 0;

        public static uint ProcessIdOf(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            return pid;
        }

        /// <summary>직계 자식 창만 z-order 순으로 돌려준다.</summary>
        public static IEnumerable<IntPtr> Children(IntPtr parent)
        {
            for (var h = GetWindow(parent, GW_CHILD); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
                yield return h;
        }

        public static RECT RectInParent(IntPtr hWnd, IntPtr parent)
        {
            GetWindowRect(hWnd, out var rect);
            MapWindowPoints(IntPtr.Zero, parent, ref rect, 2);
            return rect;
        }

        /// <summary>
        /// 대상 창과 같은 DPI 인식 모드로 현재 스레드를 잠시 바꾼다.
        /// 좌표를 대상 창 기준으로 읽고 써야 배율(125% 등) 환경에서 반올림 오차로 크기가 흔들리지 않는다.
        /// </summary>
        public readonly struct DpiScope : IDisposable
        {
            readonly IntPtr previous;

            public DpiScope(IntPtr hWnd)
            {
                var context = GetWindowDpiAwarenessContext(hWnd);
                previous = context != IntPtr.Zero ? SetThreadDpiAwarenessContext(context) : IntPtr.Zero;
            }

            public void Dispose()
            {
                if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
            }
        }
    }
}
