using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KakaoAdHider;
using static KakaoAdHider.Native;

// 실제 카카오톡을 조작하지 않는다. 테스트 프로세스가 만든 화면 밖 Win32 창만 사용한다.
internal static class Program
{
    const uint Popup = 0x80000000, Child = 0x40000000, Visible = 0x10000000;
    static readonly List<IntPtr> windows = new();
    static readonly WndProc procedure = TestWindowProc;
    static int assertions;
    static IntPtr stallTarget;
    static int stallArmed;
    static System.Threading.ManualResetEventSlim stallEntered, stallRelease;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            foreach (var name in new[] { "Qt6103QWindowIcon", "Qt6999QWindowIcon", "Chrome_WidgetWin_0",
                "Chrome_WidgetWin_1", "EVA_Window_Dblclk", "EVA_ChildWindow" })
            {
                var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Name = name,
                    Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = GetModuleHandle(null) };
                if (RegisterClassEx(ref cls) == 0) throw new System.ComponentModel.Win32Exception();
            }
            if (args.Length == 1 && args[0] == "--webview-fixture")
            {
                var embedded = Window("Chrome_WidgetWin_1", "MOMENT 광고", IntPtr.Zero, -20000, -20000, 400, 100);
                Console.WriteLine(embedded.ToInt64());
                using var timer = new Timer { Interval = 20 };
                var stop = System.Threading.Tasks.Task.Run(() => Console.ReadLine());
                timer.Tick += (_, _) => { if (stop.IsCompleted) Application.ExitThread(); };
                timer.Start();
                Application.Run();
                return 0;
            }
            TestBeta();
            TestBetaCrop();
            TestCropMovement();
            TestUnusedCropWorker();
            TestBackgroundCrop();
            TestBackgroundCropDoesNotBlock();
            TestLegacy();
            Console.WriteLine($"PASS: {assertions} assertions (synthetic Win32 windows only)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { for (var i = windows.Count - 1; i >= 0; i--) DestroyWindow(windows[i]); }
    }

    static void TestBeta()
    {
        var main = Window("Qt6103QWindowIcon", "Main", IntPtr.Zero, -20000, -20000, 400, 636);
        var popup = Window("Qt6103QWindowIcon", "KakaoTalkUI", main, -20000, -19364, 400, 100);
        var host = Window("Qt6103QWindowIcon", "KakaoTalkUI", popup, 0, 0, 400, 100, true);
        var chrome = Window("Chrome_WidgetWin_0", "", host, 0, 0, 400, 100, true);
        var ad = Window("Chrome_WidgetWin_1", "MOMENT 광고", chrome, 0, 0, 400, 100, true);
        Check(BetaLayout.MainOfAdPopup(popup) == main, "observed beta hierarchy detected");
        var ids = new HashSet<uint> { (uint)Process.GetCurrentProcess().Id };
        Check(AdHider.FindMainWindows(ids).Contains(main), "beta integrated into main discovery");
        Check(AdHider.FindMainWindows(new HashSet<uint>()).Count == 0, "foreign process excluded");
        GetWindowRect(main, out var before);
        var hider = new AdHider();
        Check(hider.Apply(main), "beta apply"); Pump();
        Check(!HasVisibleStyle(popup) && HasVisibleStyle(main), "only popup hidden");
        GetWindowRect(main, out var after);
        Check(before.Width == after.Width && before.Height == after.Height, "Qt body not resized");
        SetWindowPos(popup, IntPtr.Zero, -19980, -19364, 400, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        ShowWindowAsync(popup, SW_SHOWNA); Pump();
        Check(AdHider.FindMainWindows(ids).Contains(main), "beta discovery survives transient popup mismatch");
        Check(!new AdHider().Apply(main) && HasVisibleStyle(popup), "initial mismatch never hides unknown popup");
        Check(hider.Apply(main), "previously verified popup remains tracked during mismatch"); Pump();
        Check(!HasVisibleStyle(popup), "previously verified popup reshown during movement is hidden again");
        ShowWindowAsync(popup, SW_SHOWNA); Pump();
        SetWindowText(ad, "ordinary web content");
        Check(!hider.Apply(main) && HasVisibleStyle(popup), "changed ad marker prevents hiding known popup");
        SetWindowText(ad, "MOMENT 광고");
        hider.Apply(main); Pump();
        Check(!HasVisibleStyle(popup), "known popup hidden again after marker returns");
        SetWindowPos(popup, IntPtr.Zero, -20000, -19364, 400, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        Check(hider.Apply(main), "hidden popup remains discoverable");
        ShowWindowAsync(popup, SW_SHOWNA); Pump();
        hider.Apply(main); Pump();
        Check(!HasVisibleStyle(popup), "reshown advertisement hidden again");
        hider.RestoreAll(); Pump();
        Check(HasVisibleStyle(popup), "popup restored");

        SetWindowText(ad, "ordinary web content");
        Check(!hider.Apply(main), "ordinary web view rejected");
        Check(HasVisibleStyle(popup), "non-ad popup preserved");
        SetWindowText(ad, "MOMENT 광고");
        SetWindowPos(popup, IntPtr.Zero, -20000, -19464, 400, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        Check(BetaLayout.MainOfAdPopup(popup) == main, "visible beta ad aligned inside main bottom");
        hider.Apply(main); Pump();
        Check(!HasVisibleStyle(popup), "inside-bottom advertisement hidden");
        hider.RestoreAll(); Pump();
        SetWindowPos(popup, IntPtr.Zero, -20000, -19400, 400, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        Check(!hider.Apply(main), "popup not aligned to bottom rejected");
        SetWindowPos(popup, IntPtr.Zero, -20000, -19364, 300, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        Check(!hider.Apply(main), "wrong width rejected");
        SetWindowPos(popup, IntPtr.Zero, -20000, -19364, 400, 400, SWP_NOZORDER | SWP_NOACTIVATE);
        Check(!hider.Apply(main), "oversized popup rejected");
        SetWindowPos(popup, IntPtr.Zero, -20000, -19364, 400, 100, SWP_NOZORDER | SWP_NOACTIVATE);

        var other = Window("Qt6103QWindowIcon", "ordinary popup", main, -20000, -19364, 400, 100);
        Check(BetaLayout.MainOfAdPopup(other) == IntPtr.Zero, "Qt popup without ad rejected");
        var orphan = Window("Qt6103QWindowIcon", "KakaoTalkUI", IntPtr.Zero, -20000, -19364, 400, 100);
        Window("Chrome_WidgetWin_1", "MOMENT 광고", orphan, 0, 0, 400, 100, true);
        Check(BetaLayout.MainOfAdPopup(orphan) == IntPtr.Zero, "unowned popup rejected");
        var nextMain = Window("Qt6999QWindowIcon", "Main", IntPtr.Zero, -21000, -20000, 400, 636);
        var nextPopup = Window("Qt6999QWindowIcon", "KakaoTalkUI", nextMain, -21000, -19364, 400, 100);
        Window("Chrome_WidgetWin_1", "MOMENT 광고", nextPopup, 0, 0, 400, 100, true);
        Check(BetaLayout.MainOfAdPopup(nextPopup) == nextMain, "Qt version not hardcoded");

        // 실제 Chromium처럼 별도 프로세스의 창을 자식으로 임베드한다.
        SetWindowText(ad, "ordinary web content");
        using (var webview = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--webview-fixture")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true }))
        {
            try
            {
                var line = webview.StandardOutput.ReadLineAsync();
                var timeout = Stopwatch.StartNew();
                while (!line.IsCompleted && timeout.ElapsedMilliseconds < 10000) { Pump(); System.Threading.Thread.Sleep(10); }
                if (!line.IsCompleted) throw new Exception("webview fixture startup timed out");
                var external = new IntPtr(long.Parse(line.Result));
                SetWindowLong(external, GWL_STYLE, unchecked((int)(Child | Visible)));
                SetParent(external, chrome);
                Check(ProcessIdOf(external) != ProcessIdOf(main), "webview fixture uses separate process");
                Check(BetaLayout.MainOfAdPopup(popup) == main, "cross-process embedded ad detected");
            }
            finally
            {
                webview.StandardInput.WriteLine("stop");
                var timeout = Stopwatch.StartNew();
                while (!webview.HasExited && timeout.ElapsedMilliseconds < 10000) { Pump(); System.Threading.Thread.Sleep(10); }
                if (!webview.HasExited) webview.Kill();
            }
        }
        SetWindowText(ad, "MOMENT 광고");

        ShowWindowAsync(popup, SW_HIDE); Pump();
        hider.Apply(main); hider.RestoreAll(); Pump();
        Check(!HasVisibleStyle(popup), "previously hidden popup not restored");
        ShowWindowAsync(popup, SW_SHOWNA); Pump();
        hider.Apply(main); Pump();
        ShowWindowAsync(main, SW_HIDE); Pump();
        hider.RestoreAll(); Pump();
        Check(HasVisibleStyle(popup), "popup visibility restored even if owner hidden");
        ShowWindowAsync(main, SW_SHOWNA); ShowWindowAsync(popup, SW_SHOWNA); Pump();
        hider.Apply(main); Pump(); DestroyWindow(popup);
        hider.Prune(); hider.RestoreAll();
        Check(!IsWindow(popup), "destroyed popup pruned safely");
    }

    static void TestLegacy()
    {
        var main = Window("EVA_Window_Dblclk", "Main", IntPtr.Zero, -22000, -20000, 400, 828);
        var online = Window("EVA_ChildWindow", "OnlineMainView_test", main, 0, 31, 400, 695, true);
        var locked = Window("EVA_ChildWindow", "LockModeView_test", main, 0, 31, 400, 695, true);
        var slot = Window("EVA_ChildWindow", "", main, 0, 727, 400, 101, true);
        var popup = Window("EVA_Window_Dblclk", "", main, -22000, -19273, 400, 101);
        var hider = new AdHider();
        Check(AdHider.FindMainWindows(new HashSet<uint> { (uint)Process.GetCurrentProcess().Id }).Contains(main), "legacy discovery");
        Check(hider.Apply(main), "legacy apply"); Pump();
        Check(!HasVisibleStyle(slot) && !HasVisibleStyle(popup), "legacy slot and popup hidden");
        Check(RectInParent(online, main).Bottom == 828 && RectInParent(locked, main).Bottom == 828, "legacy views extended");
        hider.RestoreAll(); Pump();
        Check(HasVisibleStyle(slot) && HasVisibleStyle(popup), "legacy advertisements restored");
        Check(RectInParent(online, main).Bottom == 726 && RectInParent(locked, main).Bottom == 726, "legacy gap restored");
        // 광고 숨김을 적용하지 않은 인스턴스는 다른 앱/인스턴스의 배치를 바꾸지 않는다.
        SetWindowPos(online, IntPtr.Zero, 0, 0, 400, 797, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        new AdHider().RestoreAll(); Pump();
        Check(RectInParent(online, main).Bottom == 828, "untouched layout left alone");
    }

    static void TestBetaCrop()
    {
        const int left = -23000, top = -20000;
        var main = Window("Qt6103QWindowIcon", "Main", IntPtr.Zero, left, top, 400, 636);
        var popup = Window("Qt6103QWindowIcon", "KakaoTalkUI", main, left, top + 636, 400, 100);
        var ad = Window("Chrome_WidgetWin_1", "MOMENT 광고", popup, 0, 0, 400, 100, true);
        var hider = new AdHider();

        // 광고가 창 바깥에 있을 때는 본문이나 창 크기를 잘라내지 않는다.
        hider.Apply(main); Pump();
        Check(!HasWindowRegion(main), "outside-bottom beta ad does not crop main");
        GetWindowRect(main, out var originalBounds);
        SetWindowPos(popup, IntPtr.Zero, left, top + 536, 400, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        hider.Apply(main); Pump();
        CheckRegionBounds(main, 400, 536, "inside-bottom ad crop excludes white strip");
        GetWindowRect(main, out var croppedBounds);
        Check(originalBounds.Width == croppedBounds.Width && originalBounds.Height == croppedBounds.Height,
            "crop preserves Qt window geometry");
        hider.Apply(main); hider.Apply(main); Pump();
        CheckRegionBounds(main, 400, 536, "repeated crop does not progressively shrink");

        // 본문 크기 조절 후 광고의 새 높이까지 다시 표시한다.
        SetWindowPos(main, IntPtr.Zero, left, top, 450, 736, SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(popup, IntPtr.Zero, left, top + 636, 450, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        hider.Apply(main); Pump();
        CheckRegionBounds(main, 450, 636, "crop follows beta window and popup resize");
        SetWindowPos(main, IntPtr.Zero, left + 200, top + 100, 450, 736, SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(popup, IntPtr.Zero, left + 200, top + 736, 450, 100, SWP_NOZORDER | SWP_NOACTIVATE);
        hider.Apply(main); Pump();
        CheckRegionBounds(main, 450, 636, "moving beta window preserves relative crop");

        // 광고 식별이 사라지면 앱이 더는 해당 영역을 잘라내지 않아야 한다.
        SetWindowText(ad, "ordinary web content");
        hider.Prune(); Pump();
        Check(!HasWindowRegion(main), "ad marker removal restores original unbounded window");
        SetWindowText(ad, "MOMENT 광고");
        hider.Apply(main); Pump();
        CheckRegionBounds(main, 450, 636, "recognized ad can be cropped again");
        hider.RestoreAll(); Pump();
        Check(!HasWindowRegion(main), "exit restores original unbounded region");

        // 이미 사용자 지정 모양이 있으면 보이는 부분과의 교집합을 사용하고 종료 시 정확히 복구한다.
        var original = RegionApi.CreateRoundRectRgn(0, 0, 450, 736, 50, 50);
        if (original == IntPtr.Zero || RegionApi.SetWindowRgn(main, original, true) == 0)
            throw new System.ComponentModel.Win32Exception();
        var saved = CopyWindowRegion(main);
        try
        {
            hider.Apply(main); Pump();
            var cropped = CopyWindowRegion(main);
            try
            {
                Check(!RegionApi.PtInRegion(cropped, 0, 0) && RegionApi.PtInRegion(cropped, 225, 635)
                    && !RegionApi.PtInRegion(cropped, 225, 636), "crop intersects original nonrectangular region");
            }
            finally { RegionApi.DeleteObject(cropped); }
            hider.Apply(main); Pump();
            hider.RestoreAll(); Pump();
            var restored = CopyWindowRegion(main);
            try { Check(RegionApi.EqualRgn(restored, saved), "exit restores exact original custom region"); }
            finally { RegionApi.DeleteObject(restored); }
        }
        finally { RegionApi.DeleteObject(saved); }

        // 실행 중 Qt가 스스로 다른 영역을 지정했다면 종료 시 덮어쓰지 않는다.
        hider.Apply(main); Pump();
        var replacement = RegionApi.CreateRectRgn(10, 10, 440, 700);
        if (replacement == IntPtr.Zero || RegionApi.SetWindowRgn(main, replacement, true) == 0)
            throw new System.ComponentModel.Win32Exception();
        var expectedReplacement = CopyWindowRegion(main);
        try
        {
            hider.RestoreAll(); Pump();
            var actual = CopyWindowRegion(main);
            try { Check(RegionApi.EqualRgn(actual, expectedReplacement), "external region change survives exit"); }
            finally { RegionApi.DeleteObject(actual); }
        }
        finally { RegionApi.DeleteObject(expectedReplacement); }

        // 적용 중 모양이 바뀐 경우 새 모양을 기준으로 다시 잘라낸 뒤 종료 시 그 모양을 보존한다.
        hider.Apply(main); Pump();
        var changed = RegionApi.CreateRectRgn(20, 20, 430, 710);
        if (changed == IntPtr.Zero || RegionApi.SetWindowRgn(main, changed, true) == 0)
            throw new System.ComponentModel.Win32Exception();
        var expectedChanged = CopyWindowRegion(main);
        try
        {
            hider.Apply(main); Pump();
            var cropped = CopyWindowRegion(main);
            try
            {
                Check(!RegionApi.PtInRegion(cropped, 10, 30) && RegionApi.PtInRegion(cropped, 200, 635)
                    && !RegionApi.PtInRegion(cropped, 200, 636), "reapply clips new external region");
            }
            finally { RegionApi.DeleteObject(cropped); }
            hider.RestoreAll(); Pump();
            var restored = CopyWindowRegion(main);
            try { Check(RegionApi.EqualRgn(restored, expectedChanged), "exit restores latest externally changed region"); }
            finally { RegionApi.DeleteObject(restored); }
        }
        finally { RegionApi.DeleteObject(expectedChanged); }
        RegionApi.SetWindowRgn(main, IntPtr.Zero, true);

        // 반복 적용과 복원에서 GDI 영역 핸들이 누적되지 않아야 한다.
        hider.Apply(main); hider.RestoreAll(); Pump(); // GDI 및 WinForms 초기화 비용 제외
        using (var process = Process.GetCurrentProcess())
        {
            var before = RegionApi.GetGuiResources(process.Handle, 0);
            for (var i = 0; i < 128; i++) { hider.Apply(main); hider.RestoreAll(); Pump(); }
            var after = RegionApi.GetGuiResources(process.Handle, 0);
            Check(after <= before + 2, $"GDI handles remain bounded after 128 cycles (before={before}, after={after})");
        }

        ShowWindowAsync(main, 6); Pump(); // SW_MINIMIZE
        Check(RegionApi.IsIconic(main), "synthetic main minimized");
        hider.Apply(main); Pump();
        Check(!HasWindowRegion(main), "minimized main is not newly cropped");
        hider.RestoreAll(); Pump();
        ShowWindowAsync(main, 9); Pump(); // SW_RESTORE
        SetWindowPos(main, IntPtr.Zero, left + 200, top + 100, 450, 736, SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(popup, IntPtr.Zero, left + 200, top + 736, 450, 100, SWP_NOZORDER | SWP_NOACTIVATE);

        ShowWindowAsync(main, SW_HIDE); Pump();
        hider.Apply(main); Pump();
        Check(!HasWindowRegion(main), "hidden main is not newly cropped");
        hider.RestoreAll(); Pump();
        ShowWindowAsync(main, SW_SHOWNA); Pump();
        hider.Apply(main); Pump();
        CheckRegionBounds(main, 450, 636, "visible main can be cropped after hidden state");
        DestroyWindow(main);
        hider.Prune(); hider.RestoreAll();
        Check(!IsWindow(main), "destroyed cropped window pruned safely");
    }

    static void TestUnusedCropWorker()
    {
        using var process = Process.GetCurrentProcess();
        var before = process.Threads.Count;
        var worker = new BetaCropWorker();
        for (var i = 0; i < 256; i++) worker.Prune();
        process.Refresh();
        Check(process.Threads.Count <= before, "unused beta worker/prune creates no thread");
        var callbacks = 0;
        worker.Stop(() =>
        {
            callbacks++;
            worker.Prune();
            worker.Apply(IntPtr.Zero); // 종료 뒤에는 작업자를 시작하지 않는다.
            worker.Stop(() => callbacks++);
        });
        Check(callbacks == 1, "unused worker stops immediately and invokes callback once");
        process.Refresh();
        Check(process.Threads.Count <= before, "stop and late work do not start unused worker");
    }

    static void TestBackgroundCrop()
    {
        const int left = -24000, top = -20000;
        var main = Window("Qt6103QWindowIcon", "Main", IntPtr.Zero, left, top, 400, 636);
        var popup = Window("Qt6103QWindowIcon", "KakaoTalkUI", main, left, top + 536, 400, 100);
        var ad = Window("Chrome_WidgetWin_1", "MOMENT 광고", popup, 0, 0, 400, 100, true);
        var hider = new AdHider(backgroundCrop: true);
        // SetWindowRgn은 테스트 메인 스레드로 메시지를 전송한다. 기다리는 동안 메시지를 처리한다.
        for (var i = 0; i < 256; i++) hider.Apply(main);
        WaitUntil(() => HasWindowRegion(main), "background crop applied");
        CheckRegionBounds(main, 400, 536, "background burst crops once to exact ad top");
        for (var i = 0; i < 32; i++) { hider.Apply(main); hider.Prune(); Pump(); }
        CheckRegionBounds(main, 400, 536, "background repeated work does not progressively shrink");

        SetWindowText(ad, "ordinary web content");
        hider.Prune();
        WaitUntil(() => !HasWindowRegion(main), "background prune restores missing ad");
        SetWindowText(ad, "MOMENT 광고");
        hider.Apply(main);
        WaitUntil(() => HasWindowRegion(main), "background recrops restored ad");
        CheckRegionBounds(main, 400, 536, "background recrop remains correct");

        using var stopped = new System.Threading.ManualResetEventSlim(false);
        hider.StopCrop(() => stopped.Set());
        WaitUntil(() => stopped.IsSet, "background stop callback");
        Check(!HasWindowRegion(main), "background stop restores original region before callback");
        hider.RestoreAll(); Pump();
        Check(HasVisibleStyle(popup), "ads restored after crop worker stopped");
    }

    static void TestCropMovement()
    {
        const int x = -26000, y = -20000;
        var main = Window("Qt6103QWindowIcon", "Main", IntPtr.Zero, x, y, 400, 636);
        var popup = Window("Qt6103QWindowIcon", "KakaoTalkUI", main, x, y + 536, 400, 100);
        var ad = Window("Chrome_WidgetWin_1", "MOMENT 광고", popup, 0, 0, 400, 100, true);
        long time = 1000;
        var crop = new BetaWindowCrop(() => time);
        void Apply() { crop.Apply(main, BetaLayout.FindPopups(main)); Pump(); }
        void Move(IntPtr hwnd, int x1, int y1, int width, int height) =>
            SetWindowPos(hwnd, IntPtr.Zero, x1, y1, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

        // 아직 검증되지 않은 불일치 창은 크롭하지 않는다.
        Move(popup, x + 60, y + 536, 400, 100);
        Apply();
        Check(!HasWindowRegion(main), "initial inconsistent geometry is not cropped");
        Move(popup, x, y + 536, 400, 100);

        // Probe the environment on a separate synthetic window: some desktops report
        // effective rendering disabled after clearing a region even with policy ENABLED.
        var frameProbe = Window("Qt6103QWindowIcon", "Frame probe", IntPtr.Zero, x - 500, y, 400, 636);
        var forceProbeFrame = 2;
        var probeEnableHr = DwmSetWindowAttribute(frameProbe, 2, ref forceProbeFrame, sizeof(int));
        var probeBeforeHr = DwmGetWindowAttribute(frameProbe, 1, out var probeBefore, sizeof(int));
        var probeRegion = RegionApi.CreateRectRgn(0, 0, 400, 536);
        if (probeRegion == IntPtr.Zero || RegionApi.SetWindowRgn(frameProbe, probeRegion, true) == 0)
            throw new System.ComponentModel.Win32Exception();
        var probeRegionHr = DwmGetWindowAttribute(frameProbe, 1, out var probeDuring, sizeof(int));
        RegionApi.SetWindowRgn(frameProbe, IntPtr.Zero, true);
        var probeRestoreHr = DwmSetWindowAttribute(frameProbe, 2, ref forceProbeFrame, sizeof(int));
        var probeAfterHr = DwmGetWindowAttribute(frameProbe, 1, out var probeAfter, sizeof(int));
        var canVerifyFrameRestore = probeEnableHr == 0 && probeBeforeHr == 0 && probeBefore != 0
            && probeRegionHr == 0 && probeRestoreHr == 0 && probeAfterHr == 0 && probeAfter != 0;
        if (!canVerifyFrameRestore)
            Console.WriteLine($"SKIP: DWM effective restore unavailable in synthetic probe: enable={probeEnableHr}, before={probeBefore}/{probeBeforeHr}, region={probeDuring}/{probeRegionHr}, restore={probeRestoreHr}, after={probeAfter}/{probeAfterHr}");

        var dwmResult = DwmGetWindowAttribute(main, 1, out var frameBefore, sizeof(int));
        if (dwmResult == 0 && frameBefore == 0)
        {
            // 합성 팝업은 기본적으로 비클라이언트 렌더링이 꺼져 있을 수 있다.
            // 지원되는 환경에서는 테스트 창에 한해 켜서 억제·복원 전환을 검증한다.
            var enableFrame = 2; // DWMNCRP_ENABLED
            if (DwmSetWindowAttribute(main, 2, ref enableFrame, sizeof(int)) == 0)
                dwmResult = DwmGetWindowAttribute(main, 1, out frameBefore, sizeof(int));
        }
        Apply();
        CheckRegionBounds(main, 400, 536, "validated initial geometry cropped");
        if (dwmResult == 0 && frameBefore != 0)
        {
            Check(DwmGetWindowAttribute(main, 1, out var frameDuring, sizeof(int)) == 0 && frameDuring == 0,
                "DWM nonclient rendering suppressed while cropped");
        }
        else Console.WriteLine($"SKIP: DWM suppression transition unavailable (HRESULT={dwmResult}, enabled={frameBefore})");

        // Windows가 소유 팝업을 함께 옮길 수 있으므로 다시 이전 좌표로 보내 지연을 재현한다.
        Move(main, x + 30, y + 20, 400, 636);
        Move(popup, x, y + 536, 400, 100);
        time += 10;
        Apply();
        CheckRegionBounds(main, 400, 536, "main moves first without exposing ad strip");

        // 350ms 이상이 지나도 좌표가 계속 바뀌는 동안은 원래 크롭을 유지한다.
        for (var i = 1; i <= 5; i++)
        {
            Move(main, x + 30 + i * 7, y + 20, 400, 636);
            Move(popup, x, y + 536, 400, 100);
            time += 100;
            Apply();
            CheckRegionBounds(main, 400, 536, "continuous inconsistent movement retains crop");
        }

        // 메인 크기부터 바뀌어도 마지막으로 검증한 광고 높이를 재사용한다.
        Move(main, x + 100, y + 40, 450, 736);
        Move(popup, x, y + 536, 400, 100);
        time += 100;
        Apply();
        CheckRegionBounds(main, 450, 636, "main resize first uses last validated ad height");

        // 팝업이 따라잡으면 추측 높이가 아닌 실제 광고의 높이를 적용한다.
        Move(popup, x + 100, y + 40 + 656, 450, 80);
        time += 100;
        Apply();
        CheckRegionBounds(main, 450, 656, "caught-up popup uses new exact ad boundary");

        // 정지한 상태의 잘못된 배치는 대기 후 원래 영역으로 복원한다.
        Move(popup, x + 100, y + 40 + 736, 450, 80);
        time += 10;
        Apply();
        CheckRegionBounds(main, 450, 656, "outside ad starts settle period without flashing");
        time += 349;
        Apply();
        CheckRegionBounds(main, 450, 656, "crop survives 349ms stable mismatch");
        time += 1;
        Apply();
        Check(!HasWindowRegion(main), "350ms stable mismatch restores original region");
        if (canVerifyFrameRestore && dwmResult == 0 && frameBefore != 0)
            Check(DwmGetWindowAttribute(main, 1, out var frameSettled, sizeof(int)) == 0 && frameSettled != 0,
                "DWM nonclient rendering restored after geometry settles");

        Move(popup, x + 100, y + 40 + 656, 450, 80);
        time += 1;
        Apply();
        CheckRegionBounds(main, 450, 656, "valid popup recrops after settlement");
        SetWindowText(ad, "ordinary web content");
        time += 1;
        Apply();
        Check(!HasWindowRegion(main), "missing ad marker restores immediately");
        if (canVerifyFrameRestore && dwmResult == 0 && frameBefore != 0)
            Check(DwmGetWindowAttribute(main, 1, out var frameMarker, sizeof(int)) == 0 && frameMarker != 0,
                "DWM nonclient rendering restored when ad marker disappears");
        SetWindowText(ad, "MOMENT 광고");
        Apply();
        crop.RestoreAll(); Pump();
        if (canVerifyFrameRestore && dwmResult == 0 && frameBefore != 0)
            Check(DwmGetWindowAttribute(main, 1, out var frameAfter, sizeof(int)) == 0 && frameAfter != 0,
                "DWM nonclient rendering restored on exit");

        // 이동 중 다른 창 마스크가 지정되더라도 직전 광고 높이와 원래 마스크를 잃지 않는다.
        Move(popup, x + 100, y + 40 + 656, 450, 80);
        Apply();
        Move(main, x + 110, y + 50, 450, 736);
        Move(popup, x + 100, y + 40 + 656, 450, 80);
        time += 10;
        var replacement = RegionApi.CreateRectRgn(10, 10, 440, 720);
        if (replacement == IntPtr.Zero || RegionApi.SetWindowRgn(main, replacement, true) == 0)
            throw new System.ComponentModel.Win32Exception();
        var expectedReplacement = CopyWindowRegion(main);
        try
        {
            Apply();
            var current = CopyWindowRegion(main);
            try
            {
                Check(!RegionApi.PtInRegion(current, 5, 50) && RegionApi.PtInRegion(current, 200, 655)
                    && !RegionApi.PtInRegion(current, 200, 656), "external mask during move is clipped using remembered height");
            }
            finally { RegionApi.DeleteObject(current); }
            Move(main, x + 120, y + 50, 450, 736);
            Move(popup, x + 100, y + 40 + 656, 450, 80);
            time += 100;
            Apply();
            current = CopyWindowRegion(main);
            try { Check(!RegionApi.PtInRegion(current, 200, 656), "continued motion keeps crop after mask replacement"); }
            finally { RegionApi.DeleteObject(current); }
            crop.RestoreAll(); Pump();
            current = CopyWindowRegion(main);
            try { Check(RegionApi.EqualRgn(current, expectedReplacement), "mask replacement restored after pending movement"); }
            finally { RegionApi.DeleteObject(current); }
        }
        finally { RegionApi.DeleteObject(expectedReplacement); }
    }

    static void TestBackgroundCropDoesNotBlock()
    {
        const int left = -25000, top = -20000;
        var main = Window("Qt6103QWindowIcon", "Main", IntPtr.Zero, left, top, 400, 636);
        var popup = Window("Qt6103QWindowIcon", "KakaoTalkUI", main, left, top + 536, 400, 100);
        Window("Chrome_WidgetWin_1", "MOMENT 광고", popup, 0, 0, 400, 100, true);
        var worker = new BetaCropWorker();
        using var entered = new System.Threading.ManualResetEventSlim(false);
        using var release = new System.Threading.ManualResetEventSlim(false);
        using var stopped = new System.Threading.ManualResetEventSlim(false);
        using var coordinatorDone = new System.Threading.ManualResetEventSlim(false);
        stallTarget = main;
        stallEntered = entered;
        stallRelease = release;
        System.Threading.Interlocked.Exchange(ref stallArmed, 1);
        long elapsed = -1;
        bool stalled = false;
        var coordinator = new System.Threading.Thread(() =>
        {
            try
            {
                stalled = entered.Wait(5000);
                if (!stalled) return;
                var clock = Stopwatch.StartNew();
                for (var i = 0; i < 256; i++) worker.Apply(main);
                worker.Prune();
                worker.Stop(() => stopped.Set());
                elapsed = clock.ElapsedMilliseconds;
            }
            finally { release.Set(); coordinatorDone.Set(); }
        }) { IsBackground = true };
        coordinator.Start();
        worker.Apply(main);
        WaitUntil(() => coordinatorDone.IsSet, "blocked target coordinator", 10000);
        coordinator.Join();
        Check(stalled, "synthetic target stalled in window positioning message");
        Check(elapsed >= 0 && elapsed < 1000, $"queue and stop returned during stalled target ({elapsed}ms)");
        WaitUntil(() => stopped.IsSet, "stalled target worker completion", 10000);
        Check(!HasWindowRegion(main), "stalled-target worker restores before completion");
        stallTarget = IntPtr.Zero;
        stallEntered = null;
        stallRelease = null;
    }

    static void WaitUntil(Func<bool> condition, string name, int timeoutMs = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < timeoutMs)
        {
            Pump();
            System.Threading.Thread.Sleep(5);
        }
        Check(condition(), name);
    }

    static IntPtr TestWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        const uint WmWindowPosChanging = 0x0046;
        if (hwnd == stallTarget && msg == WmWindowPosChanging
            && System.Threading.Interlocked.CompareExchange(ref stallArmed, 0, 1) == 1)
        {
            stallEntered.Set();
            stallRelease.Wait(10000);
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    static bool HasWindowRegion(IntPtr hwnd)
    {
        var region = RegionApi.CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return RegionApi.GetWindowRgn(hwnd, region) != 0; }
        finally { RegionApi.DeleteObject(region); }
    }

    static IntPtr CopyWindowRegion(IntPtr hwnd)
    {
        var region = RegionApi.CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        if (RegionApi.GetWindowRgn(hwnd, region) != 0) return region;
        RegionApi.DeleteObject(region);
        throw new Exception("Window has no region");
    }

    static void CheckRegionBounds(IntPtr hwnd, int width, int height, string message)
    {
        var region = CopyWindowRegion(hwnd);
        try
        {
            Check(RegionApi.GetRgnBox(region, out var rect) != 0 && rect.Left == 0 && rect.Top == 0
                && rect.Right == width && rect.Bottom == height, message);
        }
        finally { RegionApi.DeleteObject(region); }
    }

    static class RegionApi
    {
        [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("gdi32.dll")] public static extern bool PtInRegion(IntPtr region, int x, int y);
        [DllImport("gdi32.dll")] public static extern bool EqualRgn(IntPtr left, IntPtr right);
        [DllImport("gdi32.dll")] public static extern int GetRgnBox(IntPtr region, out RECT rect);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr region);
    }

    static IntPtr Window(string cls, string title, IntPtr parent, int x, int y, int w, int h, bool child = false)
    {
        var hwnd = CreateWindowEx(0x08000080, cls, title, (child ? Child : Popup) | Visible,
            x, y, w, h, parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        windows.Add(hwnd);
        return hwnd;
    }
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); assertions++; }
    static void Pump() => Application.DoEvents();
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WindowClass
    {
        public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string Menu, Name; public IntPtr SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr hwnd, string text);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr hwnd, IntPtr parent);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
