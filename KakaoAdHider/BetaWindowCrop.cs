using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static KakaoAdHider.Native;

namespace KakaoAdHider
{
    /// <summary>
    /// Qt의 실제 창 크기를 줄이면 광고 여백도 다시 배치된다. 창 크기 대신 표시 영역만
    /// 광고 위까지 자른다. 테마 색과 무관하며 본문 크기/위치를 바꾸지 않는다.
    /// </summary>
    internal sealed class BetaWindowCrop
    {
        const int RgnAnd = 1, RgnCopy = 5;
        const int GeometrySettleMilliseconds = 350;
        readonly Func<long> milliseconds;
        readonly Dictionary<IntPtr, Crop> crops = new();

        public BetaWindowCrop(Func<long> milliseconds = null)
        {
            this.milliseconds = milliseconds ?? (() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency));
        }

        public void Apply(IntPtr main, IReadOnlyList<IntPtr> popups)
        {
            if (crops.TryGetValue(main, out var crop) && !crop.IsSameWindow(main))
            {
                Forget(main);
                crop = null;
            }
            // 최소화/트레이 상태의 좌표는 현재 본문 경계를 나타내지 않을 수 있다.
            if (!IsWindowVisible(main) || IsIconic(main)) return;
            using (new DpiScope(main))
            {
                if (!GetWindowRect(main, out var body) || body.Width <= 0 || body.Height <= 0) return;
                var bottom = body.Height;
                var matchedPopup = IntPtr.Zero;
                foreach (var popup in popups)
                {
                    if (GetWindowRect(popup, out var ad) && Math.Abs(ad.Bottom - body.Bottom) <= 2
                        && ad.Top > body.Top && ad.Top < body.Bottom)
                    {
                        if (ad.Top - body.Top < bottom)
                        {
                            bottom = ad.Top - body.Top;
                            matchedPopup = popup;
                        }
                    }
                }
                if (bottom == body.Height)
                {
                    // 이동 중 메인/팝업의 좌표는 동시에 갱신되지 않는다. 광고의 정체가 유지되고
                    // 배치가 아직 움직이는 동안에는 검증된 상대 높이를 유지한다.
                    if (crop == null || BetaLayout.OwnerOfAdPopup(crop.Popup) != main
                        || !GetWindowRect(crop.Popup, out var pendingAd))
                    {
                        Restore(main);
                        return;
                    }
                    var now = milliseconds();
                    if (!crop.GeometryPending || !SameRect(body, crop.PendingBody) || !SameRect(pendingAd, crop.PendingAd))
                    {
                        crop.GeometryPending = true;
                        crop.StableSince = now;
                        crop.PendingBody = body;
                        crop.PendingAd = pendingAd;
                    }
                    if (now - crop.StableSince >= GeometrySettleMilliseconds)
                    {
                        Restore(main);
                        return;
                    }
                    var dpi = GetDpiForWindow(main);
                    var height = dpi > 0 && crop.Dpi > 0
                        ? (int)Math.Round(crop.AdHeight * (double)dpi / crop.Dpi) : crop.AdHeight;
                    bottom = body.Height - height;
                    if (bottom <= 0) { Restore(main); return; }
                }
                else if (crop != null) crop.GeometryPending = false;

                if (!ReadRegion(main, out var current)) return;
                IntPtr desired = IntPtr.Zero, originalCopy = IntPtr.Zero, appliedCopy = IntPtr.Zero;
                var previousCrop = crop;
                try
                {
                    // Qt가 자체 마스크를 교체했다면 그것을 새 원본으로 삼는다.
                    if (crop != null && !SameRegion(current, crop.Applied))
                    {
                        // 영역만 교체하는 동안에는 DWM 프레임을 다시 켰다 끄지 않는다.
                        Forget(main, restoreFrame: false);
                        crop = null;
                    }
                    var original = crop != null ? crop.Original : current;
                    desired = CreateRectRgn(0, 0, body.Width, bottom);
                    if (desired == IntPtr.Zero) return;
                    if (original != IntPtr.Zero && CombineRgn(desired, desired, original, RgnAnd) <= 1) return;
                    // SetWindowRgn 자체가 위치 이벤트를 발생시킨다. 같은 마스크를 반복 설정하지 않는다.
                    if (SameRegion(current, desired))
                    {
                        if (crop != null)
                        {
                            RememberGeometry(crop, main, matchedPopup, body.Height - bottom);
                            SuppressFrame(main, crop);
                        }
                        return;
                    }
                    if (crop == null && current != IntPtr.Zero)
                    {
                        originalCopy = CopyRegion(current);
                        if (originalCopy == IntPtr.Zero) return;
                    }
                    appliedCopy = CopyRegion(desired);
                    // SetWindowRgn 자체가 유효 NC 렌더링 상태를 바꿀 수 있으므로 먼저 읽는다.
                    var frameEnabled = DwmGetWindowAttribute(main, 1, out var beforeFrame, sizeof(int)) == 0 && beforeFrame != 0;
                    if (appliedCopy == IntPtr.Zero || SetWindowRgn(main, desired, true) == 0) return;
                    desired = IntPtr.Zero; // 성공 시 HRGN 소유권은 Windows로 넘어간다.
                    if (crop == null)
                    {
                        crop = new Crop(main, originalCopy);
                        crop.FrameWasEnabled = previousCrop?.FrameWasEnabled ?? false;
                        crop.FramePolicySuppressed = previousCrop?.FramePolicySuppressed ?? false;
                        if (matchedPopup == IntPtr.Zero && previousCrop != null)
                        {
                            crop.Popup = previousCrop.Popup;
                            crop.AdHeight = previousCrop.AdHeight;
                            crop.Dpi = previousCrop.Dpi;
                            crop.GeometryPending = previousCrop.GeometryPending;
                            crop.StableSince = previousCrop.StableSince;
                            crop.PendingBody = previousCrop.PendingBody;
                            crop.PendingAd = previousCrop.PendingAd;
                        }
                        crops.Add(main, crop);
                        originalCopy = IntPtr.Zero;
                    }
                    Release(crop.Applied);
                    crop.Applied = appliedCopy;
                    appliedCopy = IntPtr.Zero;
                    crop.FrameWasEnabled |= frameEnabled;
                    RememberGeometry(crop, main, matchedPopup, body.Height - bottom);
                    SuppressFrame(main, crop);
                }
                finally
                {
                    Release(current);
                    Release(desired);
                    Release(originalCopy);
                    Release(appliedCopy);
                    // 영역 재설정에 실패하여 추적을 이어가지 못했으면 프레임 상태를 남겨두지 않는다.
                    if (!crops.ContainsKey(main) && previousCrop != null) RestoreFrame(main, previousCrop);
                }
            }
        }

        public void Prune(ISet<IntPtr> alreadyApplied = null)
        {
            foreach (var main in crops.Keys.ToList())
            {
                if (alreadyApplied != null && alreadyApplied.Contains(main)) continue;
                if (!crops[main].IsSameWindow(main)) Forget(main);
                else Apply(main, BetaLayout.FindPopups(main));
            }
        }

        public void RestoreAll()
        {
            foreach (var main in crops.Keys.ToList()) Restore(main);
        }

        void Restore(IntPtr main)
        {
            if (!crops.TryGetValue(main, out var crop)) return;
            if (!crop.IsSameWindow(main)) { Forget(main); return; }
            using (new DpiScope(main))
            {
                if (!ReadRegion(main, out var current)) return;
                try
                {
                    // 그 사이 앱이 바꾼 영역을 오래된 원본으로 덮어쓰지 않는다.
                    if (SameRegion(current, crop.Applied))
                    {
                        var restore = CopyRegion(crop.Original);
                        if (crop.Original != IntPtr.Zero && restore == IntPtr.Zero) return;
                        if (SetWindowRgn(main, restore, true) == 0)
                        {
                            Release(restore);
                            return; // 다음 스캔/복원에서 재시도할 수 있도록 상태를 남긴다.
                        }
                    }
                    Forget(main);
                }
                finally { Release(current); }
            }
        }

        static void RestoreFrame(IntPtr main, Crop crop)
        {
            if (crop.FrameWasEnabled && crop.IsSameWindow(main)
                && DwmGetWindowAttribute(main, 1, out var enabled, sizeof(int)) == 0 && enabled == 0)
            {
                // NCRENDERING_POLICY는 조회 가능한 속성이 아니다. 관찰한 유효 렌더링 상태를 복원한다.
                var policy = 2; // DWMNCRP_ENABLED
                DwmSetWindowAttribute(main, 2, ref policy, sizeof(int));
            }
        }

        void Forget(IntPtr main, bool restoreFrame = true)
        {
            var crop = crops[main];
            if (restoreFrame) RestoreFrame(main, crop);
            Release(crop.Original);
            Release(crop.Applied);
            crops.Remove(main);
        }

        static void RememberGeometry(Crop crop, IntPtr main, IntPtr popup, int height)
        {
            if (popup == IntPtr.Zero) return; // 임시 보정치를 새 기준으로 누적하지 않는다.
            crop.Popup = popup;
            crop.AdHeight = height;
            crop.Dpi = GetDpiForWindow(main);
        }

        static void SuppressFrame(IntPtr main, Crop crop)
        {
            // 원래 HWND 경계에 남는 DWM 프레임/그림자를 표시 영역과 함께 숨긴다.
            if (DwmGetWindowAttribute(main, 1, out var enabled, sizeof(int)) == 0
                && ((!crop.FramePolicySuppressed && crop.FrameWasEnabled) || enabled != 0))
            {
                var policy = 1; // DWMNCRP_DISABLED
                if (DwmSetWindowAttribute(main, 2, ref policy, sizeof(int)) == 0)
                {
                    crop.FrameWasEnabled |= enabled != 0;
                    crop.FramePolicySuppressed = true;
                }
            }
        }

        static bool SameRect(RECT a, RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        // NULL HRGN은 '빈 영역'이 아니라 '사용자 지정 영역 없음'을 뜻한다.
        static bool ReadRegion(IntPtr main, out IntPtr region)
        {
            region = CreateRectRgn(0, 0, 0, 0);
            if (region == IntPtr.Zero) return false;
            if (GetWindowRgn(main, region) == 0)
            {
                Release(region);
                region = IntPtr.Zero;
            }
            if (IsWindow(main)) return true;
            Release(region);
            region = IntPtr.Zero;
            return false;
        }

        static IntPtr CopyRegion(IntPtr region)
        {
            if (region == IntPtr.Zero) return IntPtr.Zero;
            var copy = CreateRectRgn(0, 0, 0, 0);
            if (copy != IntPtr.Zero && CombineRgn(copy, region, IntPtr.Zero, RgnCopy) == 0)
            {
                Release(copy);
                return IntPtr.Zero;
            }
            return copy;
        }

        static bool SameRegion(IntPtr a, IntPtr b) =>
            a == IntPtr.Zero || b == IntPtr.Zero ? a == b : EqualRgn(a, b);

        static void Release(IntPtr region) { if (region != IntPtr.Zero) DeleteObject(region); }

        sealed class Crop
        {
            readonly uint processId;
            readonly string className;
            readonly IntPtr owner;
            public readonly IntPtr Original;
            public IntPtr Applied;
            public IntPtr Popup;
            public int AdHeight;
            public uint Dpi;
            public bool GeometryPending, FrameWasEnabled, FramePolicySuppressed;
            public long StableSince;
            public RECT PendingBody, PendingAd;

            public Crop(IntPtr main, IntPtr original)
            {
                processId = ProcessIdOf(main);
                className = ClassName(main);
                owner = GetWindow(main, GW_OWNER);
                Original = original;
            }

            public bool IsSameWindow(IntPtr main) => IsWindow(main) && ProcessIdOf(main) == processId
                && ClassName(main) == className && GetWindow(main, GW_OWNER) == owner;
        }
    }
}
