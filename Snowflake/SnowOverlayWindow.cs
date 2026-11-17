using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MoonUI.Snowflake;

public sealed class SnowOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private const int WmWindowPosChanging = 0x0046;
    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newStyle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    private struct SnowParticle
    {
        public double X;
        public double Y;
        public double SpeedY;
        public double DriftSpeed;
        public double Phase;
        public double Size;
        public int SpriteIndex;
    }

    private sealed class SnowVisualHost : FrameworkElement
    {
        private readonly DrawingVisual _visual = new();
        private readonly VisualCollection _children;

        public SnowVisualHost()
        {
            _children = new VisualCollection(this) { _visual };
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            RenderOptions.SetBitmapScalingMode(_visual, BitmapScalingMode.NearestNeighbor);
            IsHitTestVisible = false;
        }

        public DrawingContext OpenContext() => _visual.RenderOpen();

        protected override int VisualChildrenCount => _children.Count;
        protected override Visual GetVisualChild(int index) => _children[index];
    }

    private const int MaxParticles = 120;
    private readonly SnowParticle[] _particles = new SnowParticle[MaxParticles];
    private readonly ImageSource[] _sprites = new ImageSource[4];
    private readonly SnowVisualHost _host = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly Stopwatch _frameTimer = new();
    private readonly Random _rng = new();
    private readonly StringBuilder _classNameBuffer = new(64);

    private double _lastTimestamp;
    private readonly double _screenWidth;
    private readonly double _screenHeight;
    private IntPtr _hwnd;

    public SnowOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = false;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _screenWidth = Width;
        _screenHeight = Height;

        _host.Width = _screenWidth;
        _host.Height = _screenHeight;
        Content = _host;

        LoadSprites();
        InitializeParticles();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += OnRenderTick;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            if (_hwnd != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(_hwnd, GwlExStyle);
                SetWindowLong(_hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);

                HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
                SendToDesktopBottom();
            }
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            pos.hwndInsertAfter = HwndBottom;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    private void SendToDesktopBottom()
    {
        if (_hwnd != IntPtr.Zero)
        {
            SetWindowPos(_hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private bool IsDesktopVisibleInForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return true;

        _classNameBuffer.Clear();
        if (GetClassName(fg, _classNameBuffer, _classNameBuffer.Capacity) > 0)
        {
            string cls = _classNameBuffer.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return true;
        }

        if (Application.Current?.MainWindow != null)
        {
            var mainHandle = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            if (fg == mainHandle) return true;
        }

        return !IsForegroundFullscreen(fg);
    }

    private static bool IsForegroundFullscreen(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out NativeRect rect)) return false;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        return w >= (int)SystemParameters.PrimaryScreenWidth && h >= (int)SystemParameters.PrimaryScreenHeight;
    }

    private void LoadSprites()
    {
        for (int i = 0; i < 4; i++)
        {
            _sprites[i] = TryLoadPackResource($"snow_{i}.png")
                          ?? TryLoadFromDisk($"snow_{i}.png")
                          ?? CreateFallbackSnowflake();
        }
    }

    private static BitmapImage? TryLoadPackResource(string fileName)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/MoonUI;component/Assets/{fileName}", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? TryLoadFromDisk(string fileName)
    {
        try
        {
            string diskPath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (!File.Exists(diskPath)) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(diskPath, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource CreateFallbackSnowflake()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(2, 0, 4, 8))));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 2, 8, 4))));
        var fallback = new DrawingImage(group);
        fallback.Freeze();
        return fallback;
    }

    private void InitializeParticles()
    {
        for (int i = 0; i < MaxParticles; i++)
        {
            ResetParticle(ref _particles[i], spawnAtTop: false);
        }
    }

    private void ResetParticle(ref SnowParticle p, bool spawnAtTop)
    {
        double scaleTier = _rng.Next(2, 5);
        p.Size = scaleTier * 6.0;
        p.X = _rng.NextDouble() * _screenWidth;
        p.Y = spawnAtTop ? -p.Size - (_rng.NextDouble() * 80.0) : _rng.NextDouble() * _screenHeight;
        p.SpeedY = 32.0 + (scaleTier * 16.0) + (_rng.NextDouble() * 12.0);
        p.DriftSpeed = 0.6 + (_rng.NextDouble() * 1.2);
        p.Phase = _rng.NextDouble() * Math.PI * 2.0;
        p.SpriteIndex = _rng.Next(0, 4);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!IsVisible)
            {
                Show();
                UpdateLayout();
                SendToDesktopBottom();
            }

            if (!_renderTimer.IsEnabled)
            {
                _frameTimer.Restart();
                _lastTimestamp = 0;
                _renderTimer.Start();
            }
        }
        else
        {
            if (_renderTimer.IsEnabled)
            {
                _renderTimer.Stop();
                _frameTimer.Stop();
            }
            if (IsVisible) Hide();
        }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (!IsDesktopVisibleInForeground())
        {
            _lastTimestamp = _frameTimer.Elapsed.TotalSeconds;
            return;
        }

        double now = _frameTimer.Elapsed.TotalSeconds;
        double dt = now - _lastTimestamp;
        _lastTimestamp = now;

        if (dt <= 0 || dt > 0.1) dt = 0.016;

        using DrawingContext dc = _host.OpenContext();

        for (int i = 0; i < MaxParticles; i++)
        {
            ref SnowParticle p = ref _particles[i];

            p.Y += p.SpeedY * dt;
            p.Phase += p.DriftSpeed * dt;
            double drawX = p.X + Math.Sin(p.Phase) * 18.0;

            if (p.Y > _screenHeight + p.Size)
            {
                ResetParticle(ref p, spawnAtTop: true);
                drawX = p.X;
            }

            dc.DrawImage(_sprites[p.SpriteIndex], new Rect(drawX, p.Y, p.Size, p.Size));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SetEnabled(false);
        base.OnClosed(e);
    }
}