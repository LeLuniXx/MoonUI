using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MoonUI
{
    public partial class App : Application
    {
        public static readonly string SettingsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "moonui_settings.txt");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            LoadAndApplySavedSettings();
        }

        private static void LoadAndApplySavedSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string[] lines = File.ReadAllLines(SettingsFilePath);
                    if (lines.Length >= 3)
                    {
                        string hex = lines[0].Trim();
                        if (double.TryParse(lines[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double opacity))
                        {
                            SetIslandOpacity(opacity);
                        }
                        if (double.TryParse(lines[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double radius))
                        {
                            SetIslandRadius(radius);
                        }
                        if (!string.IsNullOrEmpty(hex))
                        {
                            SetIslandColor(hex);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fehler beim Laden abfangen
            }
        }

        public static void SetIslandColor(string hex)
        {
            try
            {
                Color color = (Color)ColorConverter.ConvertFromString(hex);
                SolidColorBrush brush = new SolidColorBrush(color);
                brush.Freeze();

                Current.Resources["IslandBackgroundBrush"] = brush;

                foreach (Window window in Current.Windows)
                {
                    if (window is MainWindow mainWin)
                    {
                        if (mainWin.FindName("IslandBorder") is Border border)
                        {
                            border.Background = brush;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ungültigen Hex-Code abfangen
            }
        }

        public static void SetIslandOpacity(double opacity)
        {
            foreach (Window window in Current.Windows)
            {
                if (window is MainWindow mainWin)
                {
                    mainWin.Opacity = opacity;
                }
            }
        }

        public static void SetIslandRadius(double radius)
        {
            foreach (Window window in Current.Windows)
            {
                if (window is MainWindow mainWin)
                {
                    if (mainWin.FindName("IslandBorder") is Border border)
                    {
                        border.CornerRadius = new CornerRadius(0, 0, radius, radius);
                    }
                }
            }
        }
    }
}