using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RedmiBudsTray;

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RedmiBudsTray";
    private static string TaskName => "RedmiBudsTray-" + WindowsIdentity.GetCurrent().User!.Value;

    public static bool IsEnabled
    {
        get
        {
            try { return WithScheduler(folder => { try { return (bool)folder.GetTask(TaskName).Enabled; } catch (COMException) { return LegacyEnabled; } }); }
            catch { return LegacyEnabled; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        WithScheduler(folder =>
        {
            if (!enabled)
            {
                try { folder.DeleteTask(TaskName, 0); }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                return true;
            }
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用路径");
            if (!File.Exists(executable)) throw new InvalidOperationException("启动程序路径无效");
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            try
            {
                service.Connect();
                dynamic definition = service.NewTask(0);
                var sid = WindowsIdentity.GetCurrent().User!.Value;
                definition.RegistrationInfo.Description = "Redmi Buds Tray — 当前用户登录后启动耳机托盘应用";
                definition.Principal.UserId = sid;
                definition.Principal.LogonType = 3;
                definition.Principal.RunLevel = 0;
                definition.Settings.Enabled = true;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.ExecutionTimeLimit = "PT0S";
                definition.Settings.MultipleInstances = 2;
                definition.Settings.RestartInterval = "PT1M";
                definition.Settings.RestartCount = 3;
                dynamic trigger = definition.Triggers.Create(9);
                trigger.UserId = sid;
                trigger.Delay = "PT20S";
                dynamic action = definition.Actions.Create(0);
                action.Path = executable;
                action.Arguments = "--autostart";
                action.WorkingDirectory = Path.GetDirectoryName(executable)!;
                folder.RegisterTaskDefinition(TaskName, definition, 6, sid, null, 3, null);
            }
            finally { Marshal.ReleaseComObject(service); }
            return true;
        });
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(ValueName, false);
        AppLog.Write(enabled ? "Login startup task installed/repaired" : "Login startup disabled");
    }

    private static bool LegacyEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value); }
    }

    public static void RepairIfEnabled() { if (IsEnabled) SetEnabled(true); }

    // An uninstaller must never remove another portable copy's startup task.
    public static void RemoveIfOwned()
    {
        var executable = Environment.ProcessPath;
        WithScheduler(folder =>
        {
            try
            {
                dynamic task = folder.GetTask(TaskName);
                var path = (string)task.Definition.Actions.Item(1).Path;
                if (string.Equals(path, executable, StringComparison.OrdinalIgnoreCase)) folder.DeleteTask(TaskName, 0);
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
            return true;
        });
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key?.GetValue(ValueName) is string value &&
            value.StartsWith("\"" + executable + "\"", StringComparison.OrdinalIgnoreCase)) key.DeleteValue(ValueName, false);
    }

    private static T WithScheduler<T>(Func<dynamic, T> action)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folderObject = null;
        try { service.Connect(); folderObject = service.GetFolder(@"\"); return action((dynamic)folderObject); }
        finally
        {
            if (folderObject is not null) Marshal.ReleaseComObject(folderObject);
            Marshal.ReleaseComObject(service);
        }
    }
}
