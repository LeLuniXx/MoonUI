using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MoonUI;

public partial class SpriteWindow : Window
{
    private readonly DispatcherTimer _animationTimer;
    private CroppedBitmap[] _frames = [];
    private int _currentFrame;

    public SpriteWindow()
    {
        InitializeComponent();
        LoadSpriteFrames();

        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _animationTimer.Tick += (_, _) => AnimateSprite();

        Loaded += (_, _) => RestorePosition();
    }

    private void LoadSpriteFrames()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/MoonUI;component/Assets/sprite.png", UriKind.Absolute);
            var sheet = new BitmapImage();
            sheet.BeginInit();
            sheet.UriSource = uri;
            sheet.CacheOption = BitmapCacheOption.OnLoad;
            sheet.EndInit();
            sheet.Freeze();

            int frameSize = sheet.PixelWidth;
            int totalFrames = Math.Max(1, sheet.PixelHeight / frameSize);
            _frames = new CroppedBitmap[totalFrames];

            for (int i = 0; i < totalFrames; i++)
            {
                var frame = new CroppedBitmap(sheet, new Int32Rect(0, i * frameSize, frameSize, frameSize));
                frame.Freeze();
                _frames[i] = frame;
            }

            if (_frames.Length > 0) SpriteImage.Source = _frames[0];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Sprite Ladefehler: {ex.Message}");
        }
    }

    private void AnimateSprite()
    {
        if (_frames.Length <= 1) return;
        _currentFrame = (_currentFrame + 1) % _frames.Length;
        SpriteImage.Source = _frames[_currentFrame];
    }

    public void SetActiveState(bool isActive)
    {
        if (isActive)
        {
            Show();
            if (_frames.Length > 1) _animationTimer.Start();
        }
        else
        {
            _animationTimer.Stop();
            Hide();
        }
    }

    private void RestorePosition()
    {
        var saved = SettingsManager.Current;
        if (saved.SpriteLeft is { } left && saved.SpriteTop is { } top)
        {
            Left = left;
            Top = top;
        }
        else
        {
            ResetPositionToDefault();
        }
    }

    private void ResetPositionToDefault()
    {
        Left = 100;
        Top = 100;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        SettingsManager.Current.SpriteLeft = Left;
        SettingsManager.Current.SpriteTop = Top;
        SettingsManager.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        _animationTimer.Stop();
        base.OnClosed(e);
    }
}