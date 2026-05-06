using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnoraLaunchpad.Definitions;
using UnoraLaunchpad.Linux;

namespace UnoraLaunchpad;

public static class Program
{
    private static readonly string LauncherSettingsPath = "LauncherSettings/settings.json";
    private static Settings _settings;
    private static readonly UnoraClient _client = new();

    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== Unora Launchpad (Linux Fork) ===");

        _settings = FileService.LoadSettings(LauncherSettingsPath);

        if (args.Length > 0)
        {
            _settings.LutrisId = args[0];
        }

        // Auto-discovery if nothing is configured
        if (string.IsNullOrEmpty(_settings.LutrisId) && string.IsNullOrEmpty(_settings.GamePath))
        {
            Console.WriteLine("[Launcher] No configuration found. Attempting auto-discovery...");
            string targetSlug = "dark-ages--1";

            _settings.LutrisId = targetSlug;

            // Try to find the game path for DLL copying and other logic
            var discoveredPath = LutrisLauncher.GetGamePathFromConfig(targetSlug);
            if (!string.IsNullOrEmpty(discoveredPath))
            {
                _settings.GamePath = discoveredPath;
                Console.WriteLine($"[Launcher] Discovered game path: {_settings.GamePath}");
            }
            else
            {
                // Fallback to common default
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var defaultPaths = new[]
                {
                    Path.Combine(home, "Games", "dark-ages--1", "drive_c", "Program Files", "Dark Ages"),
                    Path.Combine(home, "Games", "dark-ages", "drive_c", "Program Files", "Dark Ages")
                };

                foreach (var path in defaultPaths)
                {
                    if (Directory.Exists(path))
                    {
                        _settings.GamePath = path;
                        Console.WriteLine($"[Launcher] Found game at default path: {_settings.GamePath}");
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(_settings.GamePath))
            {
                // Save settings so it doesn't have to discover every time
                FileService.SaveSettings(_settings, LauncherSettingsPath);
            }
        }

        var (ipAddress, port) = GetServerConnection();

        string winePrefix = null;
        if (!string.IsNullOrEmpty(_settings.GamePath) && _settings.GamePath.Contains("drive_c"))
        {
            winePrefix = _settings.GamePath.Substring(0, _settings.GamePath.IndexOf("drive_c"));
        }

        if (!string.IsNullOrEmpty(_settings.GamePath))
        {
            try
            {
                string sourceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
                if (!Directory.Exists(sourceDir))
                {
                    sourceDir = Path.Combine(Directory.GetCurrentDirectory(), "UnoraLaunchpad", "Resources");
                }

                if (Directory.Exists(sourceDir))
                {
                    string[] dlls = { "dawnd.dll", "ddraw.dll" };
                    foreach (var dll in dlls)
                    {
                        string src = Path.Combine(sourceDir, dll);
                        string dest = Path.Combine(_settings.GamePath, dll);
                        if (File.Exists(src))
                        {
                            Console.WriteLine($"[Launcher] Copying {dll} to game folder...");
                            File.Copy(src, dest, true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Launcher] Warning: Failed to copy windower DLLs: {ex.Message}");
            }
        }

        bool launched = false;
        Process wineProcess = null;

        // Prioritize Lutris launch if ID is provided
        if (!string.IsNullOrEmpty(_settings.LutrisId))
        {
            wineProcess = LutrisLauncher.LaunchGame(_settings.LutrisId);
            launched = true;
        }
        else if (!string.IsNullOrEmpty(_settings.GamePath))
        {
            var exeNames = new[] { "Darkages.exe", "Unora.exe" };
            foreach (var exeName in exeNames)
            {
                var fullPath = Path.Combine(_settings.GamePath, exeName);
                if (File.Exists(fullPath))
                {
                    wineProcess = LutrisLauncher.LaunchDirect(fullPath, winePrefix, _settings.LutrisId, _settings.UseDawndWindower);
                    launched = true;
                    break;
                }
            }
        }

        if (!launched)
        {
            Console.WriteLine("Error: Lutris ID not set and no GamePath found.");
            return;
        }

        if (wineProcess != null)
        {
            int wrapperPid = wineProcess.Id;
            bool isLutrisWrapper = !string.IsNullOrEmpty(_settings.LutrisId);

            if (isLutrisWrapper)
            {
                Console.WriteLine($"[Launcher] Launched Lutris wrapper PID={wrapperPid}. Waiting for game process...");
            }
            else
            {
                Console.WriteLine($"[Launcher] Launched direct WINE process PID={wrapperPid}");
                LinuxNativeMethods.kill(wrapperPid, LinuxNativeMethods.SIGSTOP);
                Console.WriteLine($"[Launcher] Freezing process {wrapperPid}...");
            }

            int targetPid = wrapperPid;
            bool peFound = false;

            // If we launched via Lutris, the wrapper isn't the game. If direct, it might be.
            if (!isLutrisWrapper)
            {
                peFound = await WaitForPeInMapsAsync(targetPid, "Darkages.exe", 2);
            }

            if (!peFound)
            {
                if (!isLutrisWrapper)
                {
                    Console.WriteLine("[Launcher] Resuming wrapper to allow child spawn...");
                    LinuxNativeMethods.kill(wrapperPid, LinuxNativeMethods.SIGCONT);
                }

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var childProcess = await LutrisLauncher.PollForProcessAsync("Darkages.exe", cts.Token);
                    if (childProcess != null)
                    {
                        targetPid = childProcess.Id;
                        LinuxNativeMethods.kill(targetPid, LinuxNativeMethods.SIGSTOP);
                        Console.WriteLine($"[Launcher] Detected and froze game process {targetPid}");
                        peFound = await WaitForPeInMapsAsync(targetPid, "Darkages.exe", 10);
                    }
                }
                catch (TaskCanceledException)
                {
                    if (isLutrisWrapper)
                    {
                        Console.WriteLine("[Launcher] Lutris failed to spawn game within timeout.");
                    }
                }

                if (!peFound && isLutrisWrapper && !string.IsNullOrEmpty(_settings.GamePath))
                {
                    Console.WriteLine("[Launcher] Falling back to direct launch...");
                    var exeNames = new[] { "Darkages.exe", "Unora.exe" };
                    foreach (var exeName in exeNames)
                    {
                        var fullPath = Path.Combine(_settings.GamePath, exeName);
                        if (File.Exists(fullPath))
                        {
                            wineProcess = LutrisLauncher.LaunchDirect(fullPath, winePrefix, _settings.LutrisId, _settings.UseDawndWindower);
                            targetPid = wineProcess.Id;
                            LinuxNativeMethods.kill(targetPid, LinuxNativeMethods.SIGSTOP);
                            peFound = await WaitForPeInMapsAsync(targetPid, "Darkages.exe", 10);
                            break;
                        }
                    }
                }
            }

            if (peFound)
            {
                try
                {
                    LinuxNativeMethods.kill(targetPid, LinuxNativeMethods.SIGSTOP);

                    PatchClient(targetPid);
                    Console.WriteLine("[Launcher] Client patched successfully.");

                    Console.WriteLine($"[Launcher] Resuming target process {targetPid}...");
                    LinuxNativeMethods.kill(targetPid, LinuxNativeMethods.SIGCONT);

                    if (wrapperPid != targetPid)
                    {
                        LinuxNativeMethods.kill(wrapperPid, LinuxNativeMethods.SIGCONT);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Launcher] Error patching client: {ex.Message}");
                    LinuxNativeMethods.kill(targetPid, LinuxNativeMethods.SIGCONT);
                    if (wrapperPid != targetPid) LinuxNativeMethods.kill(wrapperPid, LinuxNativeMethods.SIGCONT);
                }
            }
            else
            {
                Console.WriteLine($"[Launcher] Failed to find mapped PE in target process.");
                LinuxNativeMethods.kill(wrapperPid, LinuxNativeMethods.SIGCONT);
            }
        }
    }

    private static async Task<bool> WaitForPeInMapsAsync(int pid, string exeName, int timeoutSeconds)
    {
        string mapsPath = $"/proc/{pid}/maps";
        string exeNameLower = exeName.ToLower();
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            try
            {
                if (File.Exists(mapsPath))
                {
                    var lines = File.ReadAllLines(mapsPath);
                    if (lines.Any(l => l.ToLower().Contains(exeNameLower)))
                    {
                        return true;
                    }
                }
            }
            catch (Exception) { }

            await Task.Delay(10);
        }
        return false;
    }

    private static void PatchClient(int pid)
    {
        using var stream = new LinuxProcessMemoryStream(pid);
        using var patcher = new RuntimePatcher(ClientVersion.Version741, stream, true);

        var (ipAddress, port) = GetServerConnection();
        string ipStr = ipAddress.ToString();

        Console.WriteLine($"[Launcher] Redirecting to {ipStr}:{port} (Stable Methodology)");

        // 1. ORIGINAL CODE PATCHES (Safe assembly redirection)
        patcher.ApplyServerHostnamePatch(ipAddress);
        patcher.ApplyServerPortPatch(port);

        // 2. ANTI-REDIRECT PATCH (Verified stable)
        patcher.ApplyBytePatch(0x42E625, [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]);

        // 3. GAMEPLAY PATCHES
        if (_settings.SkipIntro)
            patcher.ApplySkipIntroVideoPatch();

        patcher.ApplyMultipleInstancesPatch();
        patcher.ApplyFixDarknessPatch();
    }

    private static (IPAddress, int) GetServerConnection()
    {
        if (_settings.UseLocalhost)
            return (ResolveHostname("127.0.0.1"), 4200);

        return (ResolveHostname("chaotic-minds.dynu.net"), 6900);
    }

    private static IPAddress ResolveHostname(string hostname)
    {
        try
        {
            var hostEntry = Dns.GetHostEntry(hostname);
            return hostEntry.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            if (hostname == "chaotic-minds.dynu.net") return IPAddress.Parse("71.75.133.254");
            return IPAddress.Loopback;
        }
    }

    private static string GetFilePath(string relativePath)
    {
        var baseDir = _settings.GamePath;
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.Combine(_settings.SelectedGame ?? CONSTANTS.UNORA_FOLDER_NAME);
        }
        return Path.Combine(baseDir, relativePath);
    }
}
