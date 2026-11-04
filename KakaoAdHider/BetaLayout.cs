using System;
using System.Collections.Generic;
using static KakaoAdHider.Native;

namespace KakaoAdHider
{
    /// <summary>
    /// KakaoTalkUI 26.9.2.447: Qt 메인 창 하단에 붙는 소유된 Qt 팝업 안에
    /// Chromium의 "MOMENT 광고" 창이 있다. Qt 버전 숫자나 메인 창의 언어에 의존하지 않는다.
    /// </summary>
    internal static class BetaLayout
    {
        const int Tolerance = 2;

        public static bool IsQtWindow(IntPtr hwnd)
        {
            var name = ClassName(hwnd);
            return name.StartsWith("Qt", StringComparison.Ordinal)
                && name.EndsWith("QWindowIcon", StringComparison.Ordinal);
        }

        public static IntPtr MainOfAdPopup(IntPtr popup)
        {
            var main = QtOwner(popup);
            if (main == IntPtr.Zero) return IntPtr.Zero;

            using (new DpiScope(main))
            {
                if (!GetWindowRect(main, out var body) || !GetWindowRect(popup, out var ad)
                    || body.Width <= 0 || body.Height <= 0 || ad.Height <= 0 || ad.Height > body.Height / 2
                    || Math.Abs(ad.Left - body.Left) > Tolerance || Math.Abs(ad.Right - body.Right) > Tolerance
                    || (Math.Abs(ad.Bottom - body.Bottom) > Tolerance
                        && Math.Abs(ad.Top - body.Bottom) > Tolerance)) return IntPtr.Zero;
            }

            // 깊은 자식 트리 탐색은 저렴한 소유자/크기/위치 검사가 통과한 뒤에만 한다.
            return ContainsAdView(popup, 0) ? main : IntPtr.Zero;
        }

        // 이미 확인한 창의 이동 중에는 좌표가 아니라 소유 관계와 광고 마커로 생존 여부를 확인한다.
        public static IntPtr OwnerOfAdPopup(IntPtr popup)
        {
            var main = QtOwner(popup);
            return main != IntPtr.Zero && ContainsAdView(popup, 0) ? main : IntPtr.Zero;
        }

        static IntPtr QtOwner(IntPtr popup)
        {
            if (!IsQtWindow(popup)) return IntPtr.Zero;
            var main = GetWindow(popup, GW_OWNER);
            if (main == IntPtr.Zero || !IsQtWindow(main) || GetWindow(main, GW_OWNER) != IntPtr.Zero
                || ProcessIdOf(popup) != ProcessIdOf(main)) return IntPtr.Zero;

            return main;
        }

        public static List<IntPtr> FindPopups(IntPtr main)
        {
            var popups = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                if (GetWindow(h, GW_OWNER) == main && MainOfAdPopup(h) == main) popups.Add(h);
                return true;
            }, IntPtr.Zero);
            return popups;
        }

        static bool ContainsAdView(IntPtr parent, int depth)
        {
            // 관찰된 깊이는 3이다. 알 수 없는 트리를 무제한 탐색하지 않는다.
            if (depth >= 8) return false;
            foreach (var child in Children(parent))
            {
                // 임베드된 Chromium 웹뷰는 별도 프로세스 소유다. PID는 바깥 Qt 팝업과 메인 사이에서만 확인한다.
                if (ClassName(child) == "Chrome_WidgetWin_1" && Text(child) == "MOMENT 광고") return true;
                if (ContainsAdView(child, depth + 1)) return true;
            }
            return false;
        }
    }
}
