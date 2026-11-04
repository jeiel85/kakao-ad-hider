using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KakaoAdHider
{
    /// <summary>현재 사용자 Run 키로 로그인 시 자동 실행을 켜고 끈다(관리자 권한 불필요).</summary>
    internal static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "KakaoAdHider";
        public const string SilentArg = "--startup";

        static string Command => $"\"{Application.ExecutablePath}\" {SilentArg}";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }

        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
