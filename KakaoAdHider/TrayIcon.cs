using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace KakaoAdHider
{
    /// <summary>"AD" 글자에 빗금을 친 알림 영역 아이콘을 현재 DPI 크기에 맞춰 그린다.</summary>
    internal static class TrayIcon
    {
        public static Icon Create()
        {
            var size = SystemInformation.SmallIconSize;
            float s = size.Width;
            using var bitmap = new Bitmap(size.Width, size.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using (var background = new SolidBrush(Color.FromArgb(52, 52, 52)))
                    g.FillEllipse(background, 0, 0, s - 1, s - 1);

                using (var font = new Font("Segoe UI", s * 0.36f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("AD", font, Brushes.White, new RectangleF(0, 0, s, s), format);

                using (var slash = new Pen(Color.FromArgb(235, 64, 52), s / 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(slash, s * 0.2f, s * 0.2f, s * 0.8f, s * 0.8f);
            }

            var handle = bitmap.GetHicon();
            try
            {
                using var temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                Native.DestroyIcon(handle);
            }
        }
    }
}
