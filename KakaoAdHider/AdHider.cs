using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static KakaoAdHider.Native;

namespace KakaoAdHider
{
    /// <summary>
    /// 카카오톡 PC 메인 창의 하단 배너 광고를 숨기고 그 자리를 본문으로 채운다.
    /// </summary>
    /// <remarks>
    /// 카카오톡(2026-11 확인)은 메인 창 하단에 제목 없는 빈 자식 창(EVA_ChildWindow)을 광고 슬롯으로 두고,
    /// 메인 창이 소유한 별도 팝업(EVA_Window_Dblclk, 제목 없음, 안에 Chromium 광고 뷰)을 슬롯과 같은 자리에 겹쳐 띄운다.
    /// 본문 뷰(OnlineMainView / LockModeView)는 슬롯 바로 위에서 끝나고, 창 크기가 바뀔 때마다 카카오톡이 이 배치를 다시 잡는다.
    /// 그래서 배치가 바뀔 때마다 슬롯과 팝업을 숨기고 본문 뷰를 슬롯 아래 끝까지 늘린다.
    /// 이 구조를 찾지 못하면(카카오톡 업데이트 등) 아무것도 건드리지 않는다.
    /// </remarks>
    internal sealed class AdHider
    {
        static readonly string[] ProcessNames = { "KakaoTalk", "KakaoTalkUI" };
        const string MainClass = "EVA_Window_Dblclk";
        const string SlotClass = "EVA_ChildWindow";
        const string OnlineView = "OnlineMainView";
        const string LockView = "LockModeView";
        const int Tolerance = 2;

        readonly List<HiddenWindow> hidden = new();
        readonly BetaWindowCrop betaCrop;
        readonly BetaCropWorker betaWorker;

        public AdHider(bool backgroundCrop = false)
        {
            if (backgroundCrop) betaWorker = new BetaCropWorker();
            else betaCrop = new BetaWindowCrop();
        }

        // 작업 스레드의 복원이 끝난 뒤 트레이 UI에서 나머지 복원/종료를 수행한다.
        public void StopCrop(Action completed)
        {
            if (betaWorker == null) completed();
            else betaWorker.Stop(completed);
        }

        // 메인 창별 본문 뷰와 광고 슬롯 사이의 원래 간격. 종료할 때 원래 배치로 되돌리는 데 쓴다.
        readonly Dictionary<IntPtr, int> gaps = new();

        public static HashSet<uint> FindProcessIds()
        {
            var ids = new HashSet<uint>();
            foreach (var name in ProcessNames)
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) ids.Add((uint)process.Id);
            return ids;
        }

        public static List<IntPtr> FindMainWindows(HashSet<uint> processIds)
        {
            var mains = new List<IntPtr>();
            if (processIds.Count == 0) return mains;
            EnumWindows((h, _) =>
            {
                if (!processIds.Contains(ProcessIdOf(h))) return true;
                if (ClassName(h) == MainClass
                    && Children(h).Any(c => Text(c).StartsWith(OnlineView, StringComparison.Ordinal)))
                    mains.Add(h);
                // 이동 도중 좌표가 어긋나도 이미 처리 중인 베타 창에 이벤트를 계속 전달한다.
                // 최초 숨김/크롭은 Apply에서 별도로 위치 관계를 검증한다.
                var betaMain = BetaLayout.OwnerOfAdPopup(h);
                if (betaMain != IntPtr.Zero && !mains.Contains(betaMain)) mains.Add(betaMain);
                return true;
            }, IntPtr.Zero);
            return mains;
        }

        /// <returns>광고 슬롯을 찾아 숨겼으면 true, 광고 구조가 없으면 false.</returns>
        public bool Apply(IntPtr main)
        {
            using (new DpiScope(main))
            {
                if (BetaLayout.IsQtWindow(main))
                {
                    var popups = BetaLayout.FindPopups(main);
                    foreach (var popup in popups) Hide(popup);
                    var hasKnownPopup = false;
                    // 이번에 이미 확인한 팝업은 트리를 다시 읽지 않는다. 기존 항목의 재숨김은
                    // hidden 목록에 추가하지 않으므로 별도 LINQ 목록을 만들 필요가 없다.
                    for (var i = 0; i < hidden.Count; i++)
                    {
                        var window = hidden[i];
                        if (window.Owner != main || popups.Contains(window.Hwnd)) continue;
                        if (!window.IsStillSame() || BetaLayout.OwnerOfAdPopup(window.Hwnd) != main) continue;
                        Hide(window.Hwnd);
                        hasKnownPopup = true;
                    }
                    if (betaWorker == null) betaCrop.Apply(main, popups);
                    else betaWorker.Apply(main);
                    return popups.Count > 0 || hasKnownPopup;
                }
                var layout = Layout.Read(main);
                if (layout == null) return false;

                if (!gaps.ContainsKey(main) && layout.Online.Rect.Bottom <= layout.SlotRect.Top)
                    gaps[main] = layout.SlotRect.Top - layout.Online.Rect.Bottom;

                Hide(layout.Slot);
                foreach (var popup in layout.Popups) Hide(popup);
                foreach (var view in layout.Views) Resize(view, layout.SlotRect.Bottom);
                return true;
            }
        }

        /// <summary>숨긴 광고를 다시 보이게 하고 본문 뷰를 원래 높이로 되돌린다.</summary>
        public void RestoreAll()
        {
            if (betaWorker == null) betaCrop.RestoreAll();
            // 이 인스턴스가 실제로 확장한 창만 복원한다.
            foreach (var main in gaps.Keys.ToList())
            {
                using (new DpiScope(main))
                {
                    var layout = Layout.Read(main);
                    if (layout == null) continue;
                    var bottom = layout.SlotRect.Top - gaps[main];
                    foreach (var view in layout.Views.Where(v => v.Rect.Bottom > layout.SlotRect.Top))
                        Resize(view, bottom);
                }
            }
            foreach (var window in hidden.Where(w => w.IsStillSame()))
                ShowWindowAsync(window.Hwnd, SW_SHOWNA);
            hidden.Clear();
            gaps.Clear();
        }

        /// <summary>사라진 창 핸들을 정리한다. 핸들 값이 다른 창에 재사용돼 엉뚱한 창을 되살리는 일을 막는다.</summary>
        public void Prune()
        {
            if (betaWorker == null) betaCrop.Prune();
            else betaWorker.Prune();
            hidden.RemoveAll(w => !w.IsStillSame());
            foreach (var main in gaps.Keys.Where(h => !IsWindow(h)).ToList()) gaps.Remove(main);
        }

        void Hide(IntPtr hwnd)
        {
            if (!HasVisibleStyle(hwnd)) return;
            ShowWindowAsync(hwnd, SW_HIDE);
            if (!hidden.Any(w => w.Hwnd == hwnd)) hidden.Add(HiddenWindow.Of(hwnd));
        }

        static void Resize(View view, int bottom)
        {
            if (view.Rect.Bottom == bottom || bottom <= view.Rect.Top) return;
            SetWindowPos(view.Hwnd, IntPtr.Zero, 0, 0, view.Rect.Width, bottom - view.Rect.Top,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        }

        readonly struct View
        {
            public readonly IntPtr Hwnd;
            public readonly RECT Rect; // 메인 창 클라이언트 좌표
            public readonly bool IsOnline;

            public View(IntPtr hwnd, RECT rect, bool isOnline)
            {
                Hwnd = hwnd;
                Rect = rect;
                IsOnline = isOnline;
            }
        }

        sealed class Layout
        {
            public readonly List<View> Views = new();
            public View Online;
            public IntPtr Slot;
            public RECT SlotRect; // 메인 창 클라이언트 좌표
            public readonly List<IntPtr> Popups = new();

            public static Layout Read(IntPtr main)
            {
                if (!GetClientRect(main, out var client) || client.Height <= 0) return null;

                var layout = new Layout();
                var slotCandidates = new List<IntPtr>();
                foreach (var child in Children(main))
                {
                    var title = Text(child);
                    if (title.StartsWith(OnlineView, StringComparison.Ordinal) || title.StartsWith(LockView, StringComparison.Ordinal))
                        layout.Views.Add(new View(child, RectInParent(child, main), title.StartsWith(OnlineView, StringComparison.Ordinal)));
                    else if (title.Length == 0 && ClassName(child) == SlotClass)
                        slotCandidates.Add(child);
                }

                layout.Online = layout.Views.FirstOrDefault(v => v.IsOnline);
                if (layout.Online.Hwnd == IntPtr.Zero) return null;
                var online = layout.Online.Rect;

                // 광고 슬롯: 메인 창 바닥에 붙어 있고 본문 뷰 폭 전체를 차지하는, 창 높이 절반 이하의 빈 자식 창
                foreach (var candidate in slotCandidates)
                {
                    var r = RectInParent(candidate, main);
                    if (r.Height > 0 && r.Height <= client.Height / 2 && r.Top >= online.Top
                        && r.Bottom >= client.Bottom - Tolerance
                        && r.Left <= online.Left + Tolerance && r.Right >= online.Right - Tolerance)
                    {
                        layout.Slot = candidate;
                        layout.SlotRect = r;
                        break;
                    }
                }
                if (layout.Slot == IntPtr.Zero) return null;

                // 광고 팝업: 메인 창이 소유하고 슬롯과 같은 자리에 놓인 제목 없는 창
                GetWindowRect(layout.Slot, out var slotScreen);
                EnumWindows((h, _) =>
                {
                    if (GetWindow(h, GW_OWNER) == main && ClassName(h) == MainClass && Text(h).Length == 0
                        && GetWindowRect(h, out var r) && Near(r, slotScreen))
                        layout.Popups.Add(h);
                    return true;
                }, IntPtr.Zero);
                return layout;
            }

            static bool Near(RECT a, RECT b) =>
                Math.Abs(a.Left - b.Left) <= Tolerance && Math.Abs(a.Top - b.Top) <= Tolerance
                && Math.Abs(a.Right - b.Right) <= Tolerance && Math.Abs(a.Bottom - b.Bottom) <= Tolerance;
        }

        readonly struct HiddenWindow
        {
            public readonly IntPtr Hwnd;
            public readonly IntPtr Owner;
            readonly uint processId;
            readonly string className;

            HiddenWindow(IntPtr hwnd, uint processId, string className)
            {
                Hwnd = hwnd;
                this.processId = processId;
                this.className = className;
                Owner = GetWindow(hwnd, GW_OWNER);
            }

            public static HiddenWindow Of(IntPtr hwnd) => new(hwnd, ProcessIdOf(hwnd), ClassName(hwnd));

            public bool IsStillSame() => IsWindow(Hwnd) && ProcessIdOf(Hwnd) == processId
                && ClassName(Hwnd) == className && GetWindow(Hwnd, GW_OWNER) == Owner;
        }
    }
}
