using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using static KakaoAdHider.Native;

namespace KakaoAdHider
{
    /// <summary>
    /// 알림 영역 아이콘과 감시 루프. 카카오톡 프로세스마다 WinEvent 훅을 걸어 창 표시·위치 변경에 즉시 반응하고,
    /// 훅이 놓친 경우(카카오톡 재시작 직후 등)를 위해 1초마다 한 번씩 다시 훑는다.
    /// </summary>
    internal sealed class TrayContext : ApplicationContext
    {
        const string AppTitle = "카톡 광고 숨김";

        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem startupItem;
        readonly System.Windows.Forms.Timer scanTimer;
        readonly AdHider hider = new();
        readonly Dictionary<uint, IntPtr[]> hooks = new();
        readonly WinEventProc winEventProc; // 네이티브 쪽이 콜백을 계속 쥐고 있으므로 GC되지 않게 필드로 붙잡아 둔다
        readonly SynchronizationContext ui;
        HashSet<uint> processIds = new();
        bool applyPending;

        public TrayContext(bool silent)
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            winEventProc = OnWinEvent;

            statusItem = new ToolStripMenuItem { Enabled = false };
            startupItem = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, (_, _) => ToggleStartup())
            {
                Checked = Startup.IsEnabled()
            };
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[]
            {
                statusItem,
                new ToolStripSeparator(),
                startupItem,
                new ToolStripMenuItem("종료", null, (_, _) => ExitThread())
            });

            tray = new NotifyIcon
            {
                Icon = TrayIcon.Create(),
                ContextMenuStrip = menu,
                Text = AppTitle,
                Visible = true
            };

            scanTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            scanTimer.Tick += (_, _) => Scan();
            scanTimer.Start();
            Scan();

            if (!silent)
                tray.ShowBalloonTip(4000, AppTitle, "알림 영역에서 실행 중입니다. 끄려면 아이콘을 우클릭해 [종료]를 누르세요.", ToolTipIcon.Info);
        }

        void Scan()
        {
            processIds = AdHider.FindProcessIds();
            foreach (var pid in hooks.Keys.Where(p => !processIds.Contains(p)).ToList())
                Unhook(pid);
            foreach (var pid in processIds.Where(p => !hooks.ContainsKey(p)))
                hooks[pid] = new[] { Hook(EVENT_OBJECT_SHOW, pid), Hook(EVENT_OBJECT_LOCATIONCHANGE, pid) };

            hider.Prune();
            ApplyAll();
        }

        IntPtr Hook(uint eventType, uint pid) =>
            SetWinEventHook(eventType, eventType, IntPtr.Zero, winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);

        void Unhook(uint pid)
        {
            foreach (var hook in hooks[pid].Where(h => h != IntPtr.Zero))
                UnhookWinEvent(hook);
            hooks.Remove(pid);
        }

        void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || applyPending) return;
            // 재배치 한 번에 자식 창 수십 개의 이벤트가 몰려오므로 한 번의 적용으로 묶는다.
            applyPending = true;
            ui.Post(_ => ApplyAll(), null);
        }

        void ApplyAll()
        {
            applyPending = false;
            var mains = AdHider.FindMainWindows(processIds);
            var hiding = mains.Count(hider.Apply) > 0;
            SetStatus(mains.Count == 0 ? "카카오톡 실행 대기 중"
                : hiding ? "광고 숨기는 중"
                : "카카오톡 감지됨 (광고 영역 없음)");
        }

        void SetStatus(string status)
        {
            if (statusItem.Text == status) return;
            statusItem.Text = status;
            tray.Text = $"{AppTitle} - {status}";
        }

        void ToggleStartup()
        {
            try
            {
                Startup.SetEnabled(!startupItem.Checked);
                startupItem.Checked = Startup.IsEnabled();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is System.IO.IOException)
            {
                MessageBox.Show($"자동 실행 설정을 바꾸지 못했습니다.\n{ex.Message}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void ExitThreadCore()
        {
            scanTimer.Stop();
            foreach (var pid in hooks.Keys.ToList()) Unhook(pid);
            hider.RestoreAll();
            tray.Visible = false;
            tray.Icon.Dispose();
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
