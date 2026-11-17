using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoonUI.Snowflake;

namespace MoonUI;

public enum SpriteDisplayMode
{
    Disabled,
    FloatingWindow,
    InIsland
}

public enum SearchItemKind
{
    File,
    Folder,
    App,
    Calendar
}

public sealed class SearchResultItem(string name, string path, SearchItemKind kind = SearchItemKind.File)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public SearchItemKind Kind { get; } = kind;

    public string TypeLabel => Kind switch
    {
        SearchItemKind.App => "App",
        SearchItemKind.Folder => "Ordner",
        SearchItemKind.Calendar => "Termin",
        _ => "Datei"
    };

    private ImageSource? _icon;
    public ImageSource? Icon => _icon ??= WindowsIconProvider.GetIcon(Path, Kind);
}

internal static class WindowsIconProvider
{
    private static readonly ConcurrentDictionary<string, ImageSource> ExtensionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource> ExactPathCache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr bitmapHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In] NativeSize size, [In] ShellItemImageFlags flags, out IntPtr bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [Flags]
    private enum ShellItemImageFlags
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        IconOnly = 0x04
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    private const uint ShellIconFlag = 0x000000100;
    private const uint ShellLargeIconFlag = 0x000000000;
    private const uint ShellUseFileAttributesFlag = 0x000000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    public static ImageSource? GetIcon(string path, SearchItemKind kind)
    {
        try
        {
            if (kind == SearchItemKind.Calendar)
            {
                return ExtensionCache.GetOrAdd("__calendar__", _ => ExtractLegacyIcon("calendar.ics", FileAttributeNormal, useAttributes: true)!);
            }

            if (kind == SearchItemKind.Folder)
            {
                return ExtensionCache.GetOrAdd("__folder__", _ =>
                    ExtractShellItemImage(path) ?? ExtractLegacyIcon(path, FileAttributeDirectory, useAttributes: true)!);
            }

            string ext = Path.GetExtension(path);

            if (kind == SearchItemKind.App ||
                ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                return ExactPathCache.GetOrAdd(path, p =>
                    ExtractShellItemImage(p) ?? ExtractLegacyIcon(p, FileAttributeNormal, useAttributes: false)!);
            }

            string cacheKey = string.IsNullOrEmpty(ext) ? "__no_extension__" : ext;
            return ExtensionCache.GetOrAdd(cacheKey, _ =>
                ExtractShellItemImage(path) ?? ExtractLegacyIcon(path, FileAttributeNormal, useAttributes: true)!);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Icon-Ladefehler ({path}): {ex.Message}");
            return null;
        }
    }

    private static ImageSource? ExtractShellItemImage(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;

        try
        {
            var factoryGuid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, factoryGuid, out IShellItemImageFactory factory);

            var size = new NativeSize { Width = 32, Height = 32 };
            int hr = factory.GetImage(size, ShellItemImageFlags.IconOnly | ShellItemImageFlags.BiggerSizeOk, out IntPtr bitmapHandle);

            if (hr != 0 || bitmapHandle == IntPtr.Zero)
            {
                hr = factory.GetImage(size, ShellItemImageFlags.ResizeToFit, out bitmapHandle);
            }

            if (hr == 0 && bitmapHandle != IntPtr.Zero)
            {
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        bitmapHandle,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(24, 24));
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(bitmapHandle);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ShellItemImage-Fallback ({path}): {ex.Message}");
        }

        return null;
    }

    private static ImageSource? ExtractLegacyIcon(string path, uint attributes, bool useAttributes)
    {
        var shellFileInfo = new ShellFileInfo();
        uint flags = ShellIconFlag | ShellLargeIconFlag | (useAttributes ? ShellUseFileAttributesFlag : 0);

        IntPtr result = SHGetFileInfo(path, attributes, ref shellFileInfo, (uint)Marshal.SizeOf(shellFileInfo), flags);
        if (result == IntPtr.Zero || shellFileInfo.IconHandle == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                shellFileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(shellFileInfo.IconHandle);
        }
    }
}

public partial class MainWindow
{
    private bool _isExpanded;
    private string _activeDrawerMode = "None";
    private readonly DispatcherTimer _clockTimer;
    private SpriteWindow? _spriteWindow;
    private SnowOverlayWindow? _snowOverlayWindow;

    private const int WmClipboardUpdate = 0x031D;
    private bool _ignoreNextClipboardEvent;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr windowHandle);

    private Point _dragStartPoint;
    private CancellationTokenSource? _searchCts;

    private static readonly ConcurrentDictionary<string, SearchResultItem> SearchIndex = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<FileSystemWatcher> Watchers = [];
    private static int _indexingState;

    private SpriteDisplayMode CurrentSpriteMode { get; set; } = SpriteDisplayMode.InIsland;
    private CroppedBitmap[] _cachedIslandFrames = [];
    private int _islandSpriteCurrentFrame;
    private readonly DispatcherTimer _islandSpriteTimer;

    private readonly DispatcherTimer _countdownTimer;
    private int _timerMinutes = 5;
    private TimeSpan _remainingTime = TimeSpan.FromMinutes(5);
    private bool _isTimerRunning;

    private const double BaseCollapsedWidth = 235;
    private const double BaseCollapsedHeight = 42;
    private const double BaseExpandedBarWidth = 520;
    private const double BaseExpandedBarOnlyHeight = 58;
    private const double BaseExpandedWithTimerHeight = 124;
    private const double BaseExpandedWithResultsHeight = 260;

    public MainWindow()
    {
        InitializeComponent();

        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += CountdownTimer_Tick;

        LoadIslandSpriteFrames();

        _islandSpriteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _islandSpriteTimer.Tick += IslandSpriteTimer_Tick;

        StashManager.StashUpdated += () => Dispatcher.Invoke(RefreshStashList);

        _ = Task.Run(InitializeFastSearchIndexAsync);
    }

    #region .NET 10 High-Performance Indexer & Live-Search

    private static async Task InitializeFastSearchIndexAsync()
    {
        if (Interlocked.CompareExchange(ref _indexingState, 1, 0) != 0) return;

        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appDataLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string commonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            string userStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);

            IndexStartMenuApps(commonStartMenu);
            IndexStartMenuApps(userStartMenu);

            SetupLiveWatchers(userProfile);

            var rootsToScan = new[]
            {
                userProfile,
                appDataRoaming,
                appDataLocal,
                @"C:\Program Files",
                @"C:\Program Files (x86)"
            }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

            await Parallel.ForEachAsync(rootsToScan, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (root, ct) =>
            {
                ScanDirectoryFast(root, ct);
                return ValueTask.CompletedTask;
            });

            var topDirs = new DirectoryInfo(@"C:\").EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true });
            await Parallel.ForEachAsync(topDirs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (dir, ct) =>
            {
                if (!IsIgnoredRootFolder(dir.Name) &&
                    !dir.FullName.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase) &&
                    !dir.FullName.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase))
                {
                    ScanDirectoryFast(dir.FullName, ct);
                }
                return ValueTask.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Index-Fehler: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _indexingState, 2);
        }
    }

    private static void IndexStartMenuApps(string startMenuPath)
    {
        if (!Directory.Exists(startMenuPath)) return;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true
        };

        foreach (var file in Directory.EnumerateFiles(startMenuPath, "*.lnk", options))
        {
            string appName = Path.GetFileNameWithoutExtension(file);
            if (!appName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) &&
                !appName.Contains("Deinstallieren", StringComparison.OrdinalIgnoreCase))
            {
                SearchIndex[file] = new SearchResultItem(appName, file, SearchItemKind.App);
            }
        }
    }

    private static void ScanDirectoryFast(string rootPath, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(rootPath);

        var dirOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System
        };

        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            string current = stack.Pop();

            try
            {
                string dirName = Path.GetFileName(current);
                if (!string.IsNullOrEmpty(dirName))
                {
                    SearchIndex.TryAdd(current, new SearchResultItem(dirName, current, SearchItemKind.Folder));
                }

                foreach (var file in Directory.EnumerateFiles(current, "*", dirOptions))
                {
                    string fileName = Path.GetFileName(file);
                    var kind = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                        ? SearchItemKind.App
                        : SearchItemKind.File;
                    SearchIndex.TryAdd(file, new SearchResultItem(fileName, file, kind));
                }

                foreach (var subDir in Directory.EnumerateDirectories(current, "*", dirOptions))
                {
                    string subName = Path.GetFileName(subDir);
                    if (!IsNoisyCacheFolder(subName))
                    {
                        stack.Push(subDir);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Scan übersprungen ({current}): {ex.Message}");
            }
        }
    }

    private static bool IsIgnoredRootFolder(string name) =>
        name.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PerfLogs", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Config.Msi", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoisyCacheFolder(string name) =>
        name.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Code Cache", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GPUCache", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CrashDumps", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Packages", StringComparison.OrdinalIgnoreCase);

    private static void SetupLiveWatchers(string userProfile)
    {
        try
        {
            if (!Directory.Exists(userProfile)) return;

            var watcher = new FileSystemWatcher(userProfile)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };

            watcher.Created += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.FullPath) && !IsNoisyCacheFolder(e.Name ?? ""))
                {
                    var kind = Directory.Exists(e.FullPath)
                        ? SearchItemKind.Folder
                        : (e.FullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || e.FullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? SearchItemKind.App : SearchItemKind.File);
                    SearchIndex[e.FullPath] = new SearchResultItem(Path.GetFileName(e.FullPath), e.FullPath, kind);
                }
            };

            watcher.Deleted += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.FullPath))
                {
                    SearchIndex.TryRemove(e.FullPath, out SearchResultItem? _);
                }
            };

            watcher.Renamed += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.OldFullPath))
                {
                    SearchIndex.TryRemove(e.OldFullPath, out SearchResultItem? _);
                }
                if (!string.IsNullOrEmpty(e.FullPath))
                {
                    var kind = Directory.Exists(e.FullPath)
                        ? SearchItemKind.Folder
                        : (e.FullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || e.FullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? SearchItemKind.App : SearchItemKind.File);
                    SearchIndex[e.FullPath] = new SearchResultItem(Path.GetFileName(e.FullPath), e.FullPath, kind);
                }
            };

            Watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Watcher-Fehler: {ex.Message}");
        }
    }

    private static List<SearchResultItem> QueryFastIndex(string query, CancellationToken token)
    {
        var matches = new List<(SearchResultItem Item, int Score)>();
        string cleanQuery = query.Trim();

        if (Volatile.Read(ref _indexingState) != 2)
        {
            string[] quickPaths =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            ];

            foreach (var qp in quickPaths)
            {
                if (!Directory.Exists(qp)) continue;
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(qp, $"*{cleanQuery}*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false }))
                    {
                        var kind = Directory.Exists(entry) ? SearchItemKind.Folder : SearchItemKind.File;
                        SearchIndex.TryAdd(entry, new SearchResultItem(Path.GetFileName(entry), entry, kind));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"QuickScan-Fehler: {ex.Message}");
                }
            }
        }

        foreach (var item in SearchIndex.Values)
        {
            if (token.IsCancellationRequested) break;

            string name = item.Name;
            string nameWithoutExt = Path.GetFileNameWithoutExtension(name);

            int score = 0;
            if (name.Equals(cleanQuery, StringComparison.OrdinalIgnoreCase) ||
                nameWithoutExt.Equals(cleanQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
            }
            else if (name.StartsWith(cleanQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = 75;
            }
            else if (name.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase))
            {
                score = 50;
            }

            if (score > 0)
            {
                if (item.Kind == SearchItemKind.App) score += 15;
                matches.Add((item, score));
            }
        }

        return
        [
            .. matches
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.Item.Name.Length)
                .Select(m => m.Item)
                .Where(item => File.Exists(item.Path) || Directory.Exists(item.Path))
                .Take(35)
        ];
    }

    #endregion

    private void LoadIslandSpriteFrames()
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
            _cachedIslandFrames = new CroppedBitmap[totalFrames];

            for (int i = 0; i < totalFrames; i++)
            {
                var frame = new CroppedBitmap(sheet, new Int32Rect(0, i * frameSize, frameSize, frameSize));
                frame.Freeze();
                _cachedIslandFrames[i] = frame;
            }

            if (_cachedIslandFrames.Length > 0)
            {
                IslandSpriteImage.Source = _cachedIslandFrames[0];
                IslandSpriteImage.Visibility = Visibility.Visible;

                if (_cachedIslandFrames.Length > 1)
                {
                    _islandSpriteTimer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Minecraft Sprite Ladefehler: {ex.Message}");
        }
    }

    private void IslandSpriteTimer_Tick(object? sender, EventArgs e)
    {
        if (_cachedIslandFrames.Length <= 1) return;
        _islandSpriteCurrentFrame = (_islandSpriteCurrentFrame + 1) % _cachedIslandFrames.Length;
        IslandSpriteImage.Source = _cachedIslandFrames[_islandSpriteCurrentFrame];
    }

    private void UpdateClock() => ClockTextBlock.Text = DateTime.Now.ToString("HH:mm");

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SettingsManager.Load();
        var saved = SettingsManager.Current;
        ApplyDesignTweaks(saved.Opacity, saved.CornerRadius, saved.BgHex, saved.BorderHex, save: false);
        SetSpriteDisplayMode(SpriteDisplayMode.InIsland, save: false);
        ApplyIslandScale(saved.IslandScale, animate: false);
        SetSnowOverlayEnabled(saved.EnableSnowOverlay, save: false);

        CenterIslandAtTop();
        RefreshStashList();

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle != IntPtr.Zero)
        {
            AddClipboardFormatListener(windowHandle);
            HwndSource.FromHwnd(windowHandle)?.AddHook(WndProc);
        }
    }

    public void SetSnowOverlayEnabled(bool enabled, bool save = true)
    {
        if (save)
        {
            SettingsManager.Current.EnableSnowOverlay = enabled;
            SettingsManager.Save();
        }

        if (enabled)
        {
            _snowOverlayWindow ??= new SnowOverlayWindow();
            _snowOverlayWindow.SetEnabled(true);
        }
        else
        {
            _snowOverlayWindow?.SetEnabled(false);
        }
    }

    public void RestartSnowOverlay()
    {
        bool isEnabled = SettingsManager.Current.EnableSnowOverlay;
        SetSnowOverlayEnabled(false, save: false);
        if (isEnabled)
        {
            SetSnowOverlayEnabled(true, save: false);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(windowHandle);
        }

        foreach (var w in Watchers) w.Dispose();
        Watchers.Clear();

        _clockTimer.Stop();
        _countdownTimer.Stop();
        _islandSpriteTimer.Stop();
        _spriteWindow?.Close();
        _snowOverlayWindow?.Close();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr windowHandle, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && SettingsManager.Current.AutoCaptureClipboard)
        {
            if (_ignoreNextClipboardEvent)
            {
                _ignoreNextClipboardEvent = false;
                return IntPtr.Zero;
            }

            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string? file in Clipboard.GetFileDropList())
                    {
                        if (!string.IsNullOrEmpty(file))
                            StashManager.AddFile(file);
                    }
                }
                else if (Clipboard.ContainsText())
                {
                    StashManager.AddText(Clipboard.GetText());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Clipboard-Fehler: {ex.Message}");
            }
        }

        return IntPtr.Zero;
    }

    private void Window_PreviewDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            e.Effects = DragDropEffects.Copy;

            if (!_isExpanded)
            {
                _isExpanded = true;
                CollapsedView.Visibility = Visibility.Collapsed;
                ExpandedView.Visibility = Visibility.Visible;
            }
            SetDrawerMode("Stash");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (string file in files) StashManager.AddFile(file);
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText) && e.Data.GetData(DataFormats.UnicodeText) is string text)
        {
            StashManager.AddText(text);
        }

        SetDrawerMode("Stash");
        e.Handled = true;
    }

    private void RefreshStashList()
    {
        StashListBox.ItemsSource = null;
        var limitedItems = StashManager.Items.Take(3).ToList();
        StashListBox.ItemsSource = limitedItems;
    }

    private void StashIconButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshStashList();
        SetDrawerMode("Stash");
    }

    private void ClearStashButton_Click(object sender, RoutedEventArgs e) => StashManager.ClearAll();

    private void DeleteStashItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StashItem item }) StashManager.RemoveItem(item);
    }

    private void StashListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;

        if (StashListBox.SelectedItem is StashItem item)
        {
            if (item.Type == StashItemType.Text)
            {
                _ignoreNextClipboardEvent = true;
                Clipboard.SetText(item.Content);
                CollapseIsland();
            }
            else if (item.Type == StashItemType.File)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = item.Content, UseShellExecute = true });
                    CollapseIsland();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Datei konnte nicht geöffnet werden: {ex.Message}");
                }
            }
        }
    }

    private void StashListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStartPoint = e.GetPosition(null);
            return;
        }

        Vector diff = _dragStartPoint - e.GetPosition(null);

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (StashListBox.SelectedItem is StashItem item)
            {
                var dataObj = new DataObject();
                if (item.Type == StashItemType.File && (File.Exists(item.Content) || Directory.Exists(item.Content)))
                {
                    dataObj.SetFileDropList(new StringCollection { item.Content });
                    DragDrop.DoDragDrop(StashListBox, dataObj, DragDropEffects.Copy);
                }
                else if (item.Type == StashItemType.Text)
                {
                    dataObj.SetData(DataFormats.UnicodeText, item.Content);
                    DragDrop.DoDragDrop(StashListBox, dataObj, DragDropEffects.Copy);
                }
            }
        }
    }

    private void SetSpriteDisplayMode(SpriteDisplayMode mode, bool save = true)
    {
        CurrentSpriteMode = mode;

        if (save)
        {
            SettingsManager.Current.SpriteMode = mode;
            SettingsManager.Save();
        }

        switch (mode)
        {
            case SpriteDisplayMode.Disabled:
                _spriteWindow?.SetActiveState(false);
                _islandSpriteTimer.Stop();
                IslandSpriteImage.Visibility = Visibility.Collapsed;
                break;
            case SpriteDisplayMode.FloatingWindow:
                _islandSpriteTimer.Stop();
                IslandSpriteImage.Visibility = Visibility.Collapsed;
                _spriteWindow ??= new SpriteWindow();
                _spriteWindow.SetActiveState(true);
                break;
            case SpriteDisplayMode.InIsland:
                _spriteWindow?.SetActiveState(false);
                IslandSpriteImage.Visibility = Visibility.Visible;
                if (_cachedIslandFrames.Length > 1) _islandSpriteTimer.Start();
                break;
        }

        if (!_isExpanded) AnimateIslandSize(GetCurrentCollapsedWidth(), GetCurrentCollapsedHeight());
    }

    private void CenterIslandAtTop()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2;
        Top = 0;
    }

    private double GetCurrentCollapsedWidth()
    {
        double scale = SettingsManager.Current.IslandScale;
        double width = BaseCollapsedWidth * scale;

        if (CurrentSpriteMode == SpriteDisplayMode.InIsland) width += (34 * scale);
        if (_isTimerRunning || (_remainingTime.TotalSeconds < _timerMinutes * 60 && _remainingTime.TotalSeconds > 0)) width += (70 * scale);
        return width;
    }

    private double GetCurrentCollapsedHeight() => BaseCollapsedHeight * SettingsManager.Current.IslandScale;

    public void ApplyIslandScale(double scale, bool animate = true)
    {
        if (IslandBorder != null)
        {
            IslandBorder.LayoutTransform = new ScaleTransform(scale, scale);
        }

        if (_isExpanded)
        {
            double expW = BaseExpandedBarWidth * scale;

            double expH = _activeDrawerMode switch
            {
                "Timer" => BaseExpandedWithTimerHeight * scale,
                "Results" or "Stash" => BaseExpandedWithResultsHeight * scale,
                _ => BaseExpandedBarOnlyHeight * scale
            };
            if (animate) AnimateIslandSize(expW, expH);
            else { Width = expW; Height = expH; CenterIslandAtTop(); }
        }
        else
        {
            double colW = GetCurrentCollapsedWidth();
            double colH = GetCurrentCollapsedHeight();
            if (animate) AnimateIslandSize(colW, colH);
            else { Width = colW; Height = colH; CenterIslandAtTop(); }
        }
    }

    private void AnimateIslandSize(double targetWidth, double targetHeight, Action? onCompleted = null)
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);

        var duration = TimeSpan.FromMilliseconds(180);
        var easing = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };

        double currentWidth = double.IsNaN(Width) || Width <= 0 ? targetWidth : Width;
        double currentHeight = double.IsNaN(Height) || Height <= 0 ? targetHeight : Height;

        var widthAnim = new DoubleAnimation(currentWidth, targetWidth, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        var heightAnim = new DoubleAnimation(currentHeight, targetHeight, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };

        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double targetLeft = (screenWidth - targetWidth) / 2;
        double currentLeft = double.IsNaN(Left) ? (screenWidth - currentWidth) / 2 : Left;
        var leftAnim = new DoubleAnimation(currentLeft, targetLeft, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };

        heightAnim.Completed += (_, _) =>
        {
            Width = targetWidth;
            Height = targetHeight;
            Left = targetLeft;
            onCompleted?.Invoke();
        };

        BeginAnimation(WidthProperty, widthAnim);
        BeginAnimation(HeightProperty, heightAnim);
        BeginAnimation(LeftProperty, leftAnim);
    }

    private void SetDrawerMode(string mode)
    {
        _activeDrawerMode = _activeDrawerMode == mode ? "None" : mode;

        TimerPanel.Visibility = Visibility.Collapsed;
        ResultsListBox.Visibility = Visibility.Collapsed;
        StashPanel.Visibility = Visibility.Collapsed;

        double scale = SettingsManager.Current.IslandScale;
        double expW = BaseExpandedBarWidth * scale;

        switch (_activeDrawerMode)
        {
            case "Results":
                ResultsListBox.Visibility = Visibility.Visible;
                AnimateIslandSize(expW, BaseExpandedWithResultsHeight * scale);
                break;
            case "Stash":
                StashPanel.Visibility = Visibility.Visible;
                AnimateIslandSize(expW, BaseExpandedWithResultsHeight * scale);
                break;
            case "Timer":
                TimerPanel.Visibility = Visibility.Visible;
                AnimateIslandSize(expW, BaseExpandedWithTimerHeight * scale);
                break;
            default:
                AnimateIslandSize(expW, BaseExpandedBarOnlyHeight * scale, () =>
                {
                    if (_activeDrawerMode == "None") ResultsListBox.ItemsSource = null;
                });
                break;
        }
    }

    private void IslandBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.OriginalSource is ListBox || e.OriginalSource is Button) return;
        if (_isExpanded && ExpandedView.IsMouseOver) return;

        _isExpanded = !_isExpanded;
        double scale = SettingsManager.Current.IslandScale;

        if (_isExpanded)
        {
            _activeDrawerMode = "None";
            ResultsListBox.Visibility = Visibility.Collapsed;
            TimerPanel.Visibility = Visibility.Collapsed;
            StashPanel.Visibility = Visibility.Collapsed;
            CollapsedView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;

            AnimateIslandSize(BaseExpandedBarWidth * scale, BaseExpandedBarOnlyHeight * scale, () => SearchTextBox.Focus());
        }
        else
        {
            CollapseIsland();
        }
    }

    private void CollapseIsland()
    {
        _searchCts?.Cancel();
        _isExpanded = false;
        _activeDrawerMode = "None";
        ResultsListBox.Visibility = Visibility.Collapsed;
        TimerPanel.Visibility = Visibility.Collapsed;
        StashPanel.Visibility = Visibility.Collapsed;
        ResultsListBox.ItemsSource = null;
        SearchTextBox.Text = string.Empty;

        ExpandedView.Visibility = Visibility.Collapsed;
        CollapsedView.Visibility = Visibility.Visible;

        AnimateIslandSize(GetCurrentCollapsedWidth(), GetCurrentCollapsedHeight());
    }

    private void TimerIconButton_Click(object sender, RoutedEventArgs e) => SetDrawerMode("Timer");

    private void TimerMinusButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTimerRunning || _timerMinutes <= 1) return;
        _timerMinutes--;
        _remainingTime = TimeSpan.FromMinutes(_timerMinutes);
        UpdateTimerUi();
    }

    private void TimerPlusButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTimerRunning || _timerMinutes >= 180) return;
        _timerMinutes++;
        _remainingTime = TimeSpan.FromMinutes(_timerMinutes);
        UpdateTimerUi();
    }

    private void TimerStartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTimerRunning)
        {
            _isTimerRunning = false;
            _countdownTimer.Stop();
            TimerStartStopButton.Content = "Weiter";
            TimerStartStopButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF2ECC71"));
        }
        else
        {
            if (_remainingTime.TotalSeconds <= 0) _remainingTime = TimeSpan.FromMinutes(_timerMinutes);
            _isTimerRunning = true;
            _countdownTimer.Start();
            TimerStartStopButton.Content = "Stop";
            TimerStartStopButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE74C3C"));
            CollapsedTimerPanel.Visibility = Visibility.Visible;
            UpdateTimerUi();
        }
    }

    private void TimerResetButton_Click(object sender, RoutedEventArgs e)
    {
        _isTimerRunning = false;
        _countdownTimer.Stop();
        _remainingTime = TimeSpan.FromMinutes(_timerMinutes);
        TimerStartStopButton.Content = "Start";
        TimerStartStopButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3A3F3A"));
        CollapsedTimerPanel.Visibility = Visibility.Collapsed;
        UpdateTimerUi();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        if (_remainingTime.TotalSeconds > 1)
        {
            _remainingTime = _remainingTime.Subtract(TimeSpan.FromSeconds(1));
            UpdateTimerUi();
        }
        else
        {
            _isTimerRunning = false;
            _countdownTimer.Stop();
            _remainingTime = TimeSpan.Zero;
            UpdateTimerUi();
            TimerStartStopButton.Content = "Start";
            TimerStartStopButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3A3F3A"));
            CollapsedTimerPanel.Visibility = Visibility.Collapsed;

            SystemSounds.Exclamation.Play();
            if (!_isExpanded) AnimateIslandSize(GetCurrentCollapsedWidth(), GetCurrentCollapsedHeight());
        }
    }

    private void UpdateTimerUi()
    {
        string formatted = $"{(int)_remainingTime.TotalMinutes:D2}:{_remainingTime.Seconds:D2}";
        TimerDisplayText.Text = formatted;
        CollapsedTimerText.Text = formatted;
    }

    private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            _searchCts?.Cancel();
            string query = SearchTextBox.Text.Trim();

            if (string.IsNullOrEmpty(query))
            {
                if (_activeDrawerMode == "Results") SetDrawerMode("None");
                return;
            }

            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                await Task.Delay(30, token);

                var results = await Task.Run(() => QueryFastIndex(query, token), token);

                if (token.IsCancellationRequested) return;

                ResultsListBox.ItemsSource = results;
                if (results.Count > 0) ResultsListBox.SelectedIndex = 0;

                if (_activeDrawerMode != "Results")
                {
                    SetDrawerMode("Results");
                }
            }
            catch (TaskCanceledException) { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Suchfehler: {ex.Message}");
        }
    }

    private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (ResultsListBox.SelectedItem is SearchResultItem selected) OpenResultItem(selected);
                else if (ResultsListBox.Items.Count > 0 && ResultsListBox.Items[0] is SearchResultItem first) OpenResultItem(first);
                e.Handled = true;
                break;
            case Key.Down when ResultsListBox.Items.Count > 0:
                if (ResultsListBox.SelectedIndex + 1 < ResultsListBox.Items.Count)
                {
                    ResultsListBox.SelectedIndex++;
                    ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up when ResultsListBox.Items.Count > 0:
                if (ResultsListBox.SelectedIndex - 1 >= 0)
                {
                    ResultsListBox.SelectedIndex--;
                    ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                CollapseIsland();
                e.Handled = true;
                break;
        }
    }

    private void OpenResultItem(SearchResultItem item)
    {
        if (item.Kind == SearchItemKind.Calendar) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            CollapseIsland();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Pfad konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    private void ResultsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsListBox.SelectedItem is SearchResultItem item) OpenResultItem(item);
    }

    private async void CalendarIconButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_activeDrawerMode == "Results" && ResultsListBox.ItemsSource is List<SearchResultItem> list && list.Any(x => x.Kind == SearchItemKind.Calendar))
            {
                SetDrawerMode("None");
                return;
            }

            var events = await new GoogleCalendarService().GetUpcomingEventsAsync();
            var resultItems = new List<SearchResultItem>();

            string todayFormatted = DateTime.Now.ToString("dddd, dd. MMMM", new System.Globalization.CultureInfo("de-DE"));
            resultItems.Add(new SearchResultItem($"📅 Heute: {todayFormatted}", "Heutiges Datum", SearchItemKind.Calendar));

            foreach (var evt in events)
            {
                resultItems.Add(new SearchResultItem(evt, "Google Kalender", SearchItemKind.Calendar));
            }

            ResultsListBox.ItemsSource = resultItems;
            SetDrawerMode("Results");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Kalender-Fehler: {ex.Message}");
        }
    }

    public void ApplyDesignTweaks(double opacity, double cornerRadius, string bgHex, string borderHex, bool save = true)
    {
        try
        {
            var bgColor = (Color)ColorConverter.ConvertFromString(bgHex);
            bgColor.A = (byte)(opacity * 255);
            IslandBorder.Background = new SolidColorBrush(bgColor);

            var borderColor = (Color)ColorConverter.ConvertFromString(borderHex);
            IslandBorder.BorderBrush = new SolidColorBrush(borderColor);
            IslandBorder.CornerRadius = new CornerRadius(0, 0, cornerRadius, cornerRadius);

            if (save)
            {
                SettingsManager.Current.Opacity = opacity;
                SettingsManager.Current.CornerRadius = cornerRadius;
                SettingsManager.Current.BgHex = bgHex;
                SettingsManager.Current.BorderHex = borderHex;
                SettingsManager.Save();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Design-Fehler: {ex.Message}");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new SettingsWindow { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Einstellungen konnten nicht geöffnet werden: {ex.Message}");
        }
    }
}