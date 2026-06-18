// SPDX-License-Identifier: GPL-3.0-only
//
// OsdService — 性能模式切换 OSD 提示
// ===================================
// 职责：
//   在模式切换时显示 OSD 提示（底部居中，带模式主题色）。
//   使用 WinForms Form + UpdateLayeredWindow 实现逐像素 alpha 透明。
//   支持淡入/淡出动画。

using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public sealed class OsdService : IDisposable
{
    // ── OSD 配置 ──────────────────────────────────────────

    private const int BoxWidth = 360;
    private const int BoxHeight = 64;
    private const int FadeInMs = 200;
    private const int ShowMs = 2300;
    private const int FadeOutMs = 350;
    private const int CooldownMs = 3000;

    private static readonly (string Mode, string Label, Color Color)[] ModeInfo =
    {
        ("silent", "安静模式", Color.FromArgb(76, 175, 80)),
        ("office", "均衡模式", Color.FromArgb(33, 150, 243)),
        ("beast",  "野兽模式", Color.FromArgb(255, 152, 0)),
        ("gaming", "斗战模式", Color.FromArgb(244, 67, 54)),
    };

    // ── 状态 ─────────────────────────────────────────────

    private readonly BlockingCollection<string> _queue = new();
    private readonly Thread _uiThread;
    private string? _lastMode;
    private DateTime _lastShowTime = DateTime.MinValue;
    private bool _disposed;

    public OsdService()
    {
        _uiThread = new Thread(Run)
        {
            IsBackground = true,
            Name = "OSD-UI"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        AppLog.Write("OSD", "Service started");
    }

    public void Show(string mode)
    {
        if (_disposed) return;
        try { _queue.TryAdd(mode, 50); }
        catch { /* queue full or disposed, ignore */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.CompleteAdding();
        _uiThread.Join(1000);
    }

    // ── UI 线程主循环 ─────────────────────────────────────

    private void Run()
    {
        // 使用隐藏消息窗口驱动消息循环
        using var hiddenForm = new MessageOnlyForm();
        
        Task.Run(() =>
        {
            foreach (var mode in _queue.GetConsumingEnumerable())
            {
                hiddenForm.Invoke(() => ShowOsdWindow(mode));
            }
        });

        Application.Run(hiddenForm);
    }

    // ── 显示 OSD 窗口 ─────────────────────────────────────

    private void ShowOsdWindow(string mode)
    {
        // 冷却：同一模式 3 秒内不重复
        var now = DateTime.Now;
        if (mode == _lastMode && (now - _lastShowTime).TotalMilliseconds < CooldownMs)
            return;

        _lastMode = mode;
        _lastShowTime = now;

        var info = Array.Find(ModeInfo, m => m.Mode == mode);
        if (info.Mode == null)
            info = ("office", mode, Color.FromArgb(33, 150, 243));

        AppLog.Write("OSD", $"Showing OSD: {info.Label} ({mode})");

        var screen = Screen.PrimaryScreen!.Bounds;
        var bmp = RenderOsdBitmap(info.Label, info.Color);

        var osdForm = new OsdForm(bmp, BoxWidth, BoxHeight);
        osdForm.StartPosition = FormStartPosition.Manual;
        osdForm.Location = new Point(
            (screen.Width - BoxWidth) / 2,
            screen.Height - BoxHeight - 80);

        osdForm.Show();
        osdForm.StartAnimation(FadeInMs, ShowMs, FadeOutMs);
    }

    // ── 渲染 OSD 位图 ─────────────────────────────────────

    private static Bitmap RenderOsdBitmap(string modeLabel, Color accentColor)
    {
        var bmp = new Bitmap(BoxWidth, BoxHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        g.Clear(Color.Transparent);

        const int borderWidth = 6;
        var outerRect = new Rectangle(0, 0, BoxWidth - 1, BoxHeight - 1);
        var innerRect = new Rectangle(borderWidth, borderWidth, BoxWidth - 1 - borderWidth * 2, BoxHeight - 1 - borderWidth * 2);

        // 外层胶囊形 - 边框颜色
        using var outerPath = CreateCapsulePath(outerRect);
        using (var brush = new SolidBrush(accentColor))
            g.FillPath(brush, outerPath);

        // 内层胶囊形 - 背景色
        using var innerPath = CreateCapsulePath(innerRect);
        using (var brush = new SolidBrush(Color.FromArgb(230, 24, 24, 28)))
            g.FillPath(brush, innerPath);

        // 文字
        using (var font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.White))
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString($"已切换到 {modeLabel}", font, brush, BoxWidth / 2f, BoxHeight / 2f, sf);
        }

        return bmp;
    }

    // 创建胶囊形路径（左右半圆）
    private static GraphicsPath CreateCapsulePath(Rectangle rect)
    {
        var path = new GraphicsPath();
        int d = rect.Height; // 直径 = 高度，形成左右半圆

        // 左侧半圆
        path.AddArc(rect.X, rect.Y, d, rect.Height, 90, 180);
        // 右侧半圆
        path.AddArc(rect.Right - d, rect.Y, d, rect.Height, 270, 180);
        path.CloseFigure();

        return path;
    }

    // ── 仅消息窗口 ─────────────────────────────────────────

    private class MessageOnlyForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style = 0;
                cp.ExStyle = 0;
                return cp;
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            // 永远不可见，但创建句柄以驱动消息循环
            if (!IsHandleCreated)
            {
                CreateHandle();
                base.SetVisibleCore(false);
            }
        }
    }

    // ── OSD 分层窗口 ──────────────────────────────────────

    private class OsdForm : Form
    {
        private readonly Bitmap _bitmap;
        private System.Windows.Forms.Timer? _timer;
        private int _ticks;
        private int _fadeInTicks, _showTicks, _fadeOutTicks;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (click-through)
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        public OsdForm(Bitmap bitmap, int width, int height)
        {
            _bitmap = bitmap;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Size = new Size(width, height);
            BackColor = Color.Black;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            SetLayeredBitmap(0);
        }

        public void StartAnimation(int fadeInMs, int showMs, int fadeOutMs)
        {
            _fadeInTicks = fadeInMs / 30;
            _showTicks = showMs / 30;
            _fadeOutTicks = fadeOutMs / 30;
            _ticks = 0;

            _timer = new System.Windows.Forms.Timer { Interval = 30 };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _ticks++;

            if (_ticks <= _fadeInTicks)
            {
                byte alpha = (byte)(255 * _ticks / _fadeInTicks);
                SetLayeredBitmap(alpha);
            }
            else if (_ticks <= _fadeInTicks + _showTicks)
            {
                SetLayeredBitmap(255);
            }
            else if (_ticks <= _fadeInTicks + _showTicks + _fadeOutTicks)
            {
                int fadeTicks = _ticks - _fadeInTicks - _showTicks;
                byte alpha = (byte)(255 - 255 * fadeTicks / _fadeOutTicks);
                SetLayeredBitmap(alpha);
            }
            else
            {
                _timer?.Stop();
                _timer?.Dispose();
                Close();
            }
        }

        private void SetLayeredBitmap(byte alpha)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memDc = CreateCompatibleDC(screenDc);
            var hBitmap = _bitmap.GetHbitmap(Color.FromArgb(0));
            var oldBitmap = SelectObject(memDc, hBitmap);

            var blend = new BLENDFUNCTION
            {
                BlendOp = 0, // AC_SRC_OVER
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = 1 // AC_SRC_ALPHA
            };

            var ptDst = new POINT { X = Left, Y = Top };
            var size = new SIZE { cx = Width, cy = Height };
            var ptSrc = new POINT { X = 0, Y = 0 };

            UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, 2);

            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                _bitmap.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
            ref BLENDFUNCTION pblend, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }
    }
}
