using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace ZeusCastEnhanced.Services;

public sealed class StartupOverrideService
{
    private const string OriginalTaskName = "ZEUS Switch Utility";
    private const string EnhancedTaskName = "Zeus Cast Enhanced";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string OriginalRunValue = "ZEUS Switch Utility";
    private const string EnhancedRunValue = "Zeus Cast Enhanced";
    private static readonly string OriginalSwitchUtility = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "GAMDIAS",
        "ZEUS CAST",
        "ZEUS Switch Utility.exe");

    public void StopOriginalProcesses()
    {
        foreach (var processName in new[] { "ZEUS CAST", "ZEUS Switch Utility" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(3000);
                }
                catch
                {
                }
            }
        }
    }

    public async Task<string> ApplyOverrideAsync()
    {
        StopOriginalProcesses();
        var executablePath = ResolveExecutablePath();
        var createResult = await RunTaskSchedulerAsync(
            "/Create", "/TN", EnhancedTaskName, "/TR", $"\"{executablePath}\" --background", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        if (createResult.ExitCode != 0)
        {
            return $"Could not create Enhanced startup task: {createResult.Error}";
        }

        await RunTaskSchedulerAsync("/Change", "/TN", OriginalTaskName, "/Disable");
        RemoveOriginalRunEntry();
        RemoveUserRunEntry();
        return "Enhanced now replaces the original Zeus Cast at sign-in.";
    }

    public async Task<string> RestoreOriginalAsync()
    {
        await RunTaskSchedulerAsync("/Delete", "/TN", EnhancedTaskName, "/F");
        await RunTaskSchedulerAsync("/Change", "/TN", OriginalTaskName, "/Enable");
        RestoreOriginalRunEntry();
        return "Original Zeus Cast startup has been restored.";
    }

    private static string ResolveExecutablePath()
    {
        var assemblyPath = Assembly.GetEntryAssembly()?.Location ?? string.Empty;
        var appHostPath = Path.ChangeExtension(assemblyPath, ".exe");
        return File.Exists(appHostPath) ? appHostPath : Environment.ProcessPath ?? appHostPath;
    }

    private static void RemoveOriginalRunEntry()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var runKey = localMachine.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(OriginalRunValue, false);
        }
        catch
        {
        }
    }

    private static void RestoreOriginalRunEntry()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var runKey = localMachine.CreateSubKey(RunKeyPath, writable: true);
            runKey.SetValue(OriginalRunValue, $"\"{OriginalSwitchUtility}\"");
        }
        catch
        {
        }
    }

    private static void RemoveUserRunEntry()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(EnhancedRunValue, false);
        }
        catch
        {
        }
    }

    private static async Task<(int ExitCode, string Error)> RunTaskSchedulerAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return (-1, "Task Scheduler could not be started.");
        }

        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        var output = await outputTask;
        var detail = string.IsNullOrWhiteSpace(error) ? output : error;
        return (process.ExitCode, detail.Trim());
    }
}
