using Microsoft.Win32;

namespace Vaultix.App.Services;

public sealed class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string VaultixRegistryPath = @"Software\Vaultix";
    private const string ValueName = "Vaultix Desktop";
    private const string ConfiguredValueName = "AutoStartConfigured";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
    }

    public bool GetInitialState()
    {
        using var settings = Registry.CurrentUser.CreateSubKey(VaultixRegistryPath, writable: true);
        if (settings.GetValue(ConfiguredValueName) is not int configured || configured != 1)
        {
            SetEnabled(true);
            return true;
        }

        return IsEnabled;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }


        using var settings = Registry.CurrentUser.CreateSubKey(VaultixRegistryPath, writable: true);
        settings.SetValue(ConfiguredValueName, 1, RegistryValueKind.DWord);
    }
}
