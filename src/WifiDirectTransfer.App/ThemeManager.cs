using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using WifiDirectTransfer.Core;

namespace WifiDirectTransfer.App;

/// <summary>
/// 在 Light.xaml / Dark.xaml 之间切换应用资源字典。
/// 所有主题颜色都通过 DynamicResource 引用，因此已打开的窗口会立即更新。
/// </summary>
internal static class ThemeManager
{
    private const string LightThemePath = "Themes/Light.xaml";
    private const string DarkThemePath = "Themes/Dark.xaml";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);

    private static AppThemeMode _mode = AppThemeMode.System;
    private static bool _initialized;

    public static bool IsDark { get; private set; }

    public static void Initialize(AppThemeMode mode)
    {
        if (_initialized)
        {
            Apply(mode);
            return;
        }

        _initialized = true;
        Apply(mode);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(
                (sender, _) => ApplyWindowChrome((Window)sender)));
    }

    public static void Apply(AppThemeMode mode)
    {
        _mode = mode;

        var useDark = !SystemParameters.HighContrast &&
                      (mode == AppThemeMode.Dark ||
                       (mode == AppThemeMode.System && IsSystemDark()));

        SetThemeDictionary(useDark);
        IsDark = useDark;
        ApplyWindowChromeToOpenWindows();
    }

    private static void ApplyWindowChromeToOpenWindows()
    {
        var app = Application.Current;

        if (app is null)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            ApplyWindowChrome(window);
        }
    }

    private static void ApplyWindowChrome(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;

            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var value = IsDark ? 1 : 0;
            var result = DwmSetWindowAttribute(
                hwnd,
                DwmwaUseImmersiveDarkMode,
                ref value,
                sizeof(int));

            if (result != 0)
            {
                DwmSetWindowAttribute(
                    hwnd,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref value,
                    sizeof(int));
            }
        }
        catch (Exception)
        {
            // 系统不支持深色标题栏时保持默认外观。
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);

            return value is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SetThemeDictionary(bool dark)
    {
        var app = Application.Current;

        if (app is null)
        {
            return;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(
            dictionary => dictionary.Source is not null &&
                          (dictionary.Source.OriginalString.EndsWith(
                               "Light.xaml",
                               StringComparison.OrdinalIgnoreCase) ||
                           dictionary.Source.OriginalString.EndsWith(
                               "Dark.xaml",
                               StringComparison.OrdinalIgnoreCase)));
        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                dark ? DarkThemePath : LightThemePath,
                UriKind.Relative)
        };

        if (existing is null)
        {
            dictionaries.Insert(0, replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(existing)] = replacement;
        }
    }

    private static void OnUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (_mode == AppThemeMode.System)
        {
            DispatchApply();
        }
    }

    private static void OnSystemParametersChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            DispatchApply();
        }
    }

    private static void DispatchApply()
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(
            new Action(() => Apply(_mode)));
    }
}
