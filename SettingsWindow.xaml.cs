using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MoonUI;

public partial class SettingsWindow : Window
{
    private readonly bool _isInitialized;

    public SettingsWindow()
    {
        InitializeComponent();
        LoadCurrentSettingsToUI();
        _isInitialized = true;
    }

    private void LoadCurrentSettingsToUI()
    {
        try
        {
            SettingsManager.Load();
            var saved = SettingsManager.Current;

            OpacitySlider.Value = saved.Opacity;
            OpacityValText.Text = $"{(int)(saved.Opacity * 100)}%";

            RadiusSlider.Value = saved.CornerRadius;
            RadiusValText.Text = $"{(int)saved.CornerRadius}px";

            ScaleSlider.Value = saved.IslandScale;
            ScaleValText.Text = $"{(int)(saved.IslandScale * 100)}%";

            HexColorTextBox.Text = saved.BgHex;
            SettingsHexColorTextBox.Text = saved.SettingsBgHex;
            ApplySettingsBackground(saved.SettingsBgHex);

            SnowToggle.IsChecked = saved.EnableSnowOverlay;
        }
        catch { }
    }

    private void ApplySettingsBackground(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            RootWindowBorder.Background = new SolidColorBrush(color);
        }
        catch { }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        double opacity = OpacitySlider.Value;
        OpacityValText.Text = $"{(int)(opacity * 100)}%";
        SettingsManager.Current.Opacity = opacity;

        if (Owner is MainWindow mainWin)
        {
            mainWin.ApplyDesignTweaks(opacity, SettingsManager.Current.CornerRadius, SettingsManager.Current.BgHex, SettingsManager.Current.BorderHex);
        }
    }

    private void RadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        double radius = RadiusSlider.Value;
        RadiusValText.Text = $"{(int)radius}px";
        SettingsManager.Current.CornerRadius = radius;

        if (Owner is MainWindow mainWin)
        {
            mainWin.ApplyDesignTweaks(SettingsManager.Current.Opacity, radius, SettingsManager.Current.BgHex, SettingsManager.Current.BorderHex);
        }
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        double scale = ScaleSlider.Value;
        ScaleValText.Text = $"{(int)(scale * 100)}%";
        SettingsManager.Current.IslandScale = scale;

        if (Owner is MainWindow mainWin)
        {
            mainWin.ApplyIslandScale(scale);
        }
    }

    private void HexColorTextBox_TextChanged(object sender, TextChangedEventArgs e) { }
    
    private void SettingsHexColorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized) return;
        try
        {
            string hex = SettingsHexColorTextBox.Text.Trim();
            if (!hex.StartsWith('#')) hex = "#" + hex;
            ApplySettingsBackground(hex);
        }
        catch { }
    }

    private void SaveColorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string islandHex = HexColorTextBox.Text.Trim();
            if (!islandHex.StartsWith('#')) islandHex = "#" + islandHex;
            ColorConverter.ConvertFromString(islandHex);

            string settingsHex = SettingsHexColorTextBox.Text.Trim();
            if (!settingsHex.StartsWith('#')) settingsHex = "#" + settingsHex;
            ColorConverter.ConvertFromString(settingsHex);

            SettingsManager.Current.BgHex = islandHex;
            SettingsManager.Current.SettingsBgHex = settingsHex;
            SettingsManager.Current.Opacity = OpacitySlider.Value;
            SettingsManager.Current.CornerRadius = RadiusSlider.Value;
            SettingsManager.Current.IslandScale = ScaleSlider.Value;

            SettingsManager.Save();

            if (Owner is MainWindow mainWin)
            {
                mainWin.ApplyDesignTweaks(SettingsManager.Current.Opacity, SettingsManager.Current.CornerRadius, islandHex, SettingsManager.Current.BorderHex);
                mainWin.ApplyIslandScale(SettingsManager.Current.IslandScale);
            }

            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fehler beim Speichern (ungültiger Hex-Farbcode?): {ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetColorButton_Click(object sender, RoutedEventArgs e)
    {
        const string defaultIslandHex = "#FF111215";
        const string defaultSettingsHex = "#FF181A19";

        HexColorTextBox.Text = defaultIslandHex;
        SettingsHexColorTextBox.Text = defaultSettingsHex;

        SettingsManager.Current.BgHex = defaultIslandHex;
        SettingsManager.Current.SettingsBgHex = defaultSettingsHex;

        ScaleSlider.Value = 1.0;
        OpacitySlider.Value = 1.0;
        RadiusSlider.Value = 16;
        SettingsManager.Save();

        ApplySettingsBackground(defaultSettingsHex);

        if (Owner is MainWindow mainWin)
        {
            mainWin.ApplyDesignTweaks(1.0, 16, defaultIslandHex, "#33FFFFFF");
            mainWin.ApplyIslandScale(1.0);
        }
    }

    private void SnowToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        bool isEnabled = SnowToggle.IsChecked == true;

        if (Owner is MainWindow mainWin)
        {
            mainWin.SetSnowOverlayEnabled(isEnabled);
        }
    }
}