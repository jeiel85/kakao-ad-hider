using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace KakaoAdHider
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            using var mutex = new Mutex(true, @"Local\KakaoAdHider", out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show("이미 실행 중입니다. 작업 표시줄 알림 영역(숨겨진 아이콘)을 확인하세요.", "카톡 광고 숨김",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext(silent: args.Contains(Startup.SilentArg)));
        }
    }
}
