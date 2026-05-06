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

        // Auto-discovery if GamePath is missing
        if (string.IsNullOrEmpty(_settings.GamePath))
        {
            Console.WriteLine("[Launcher] Game path not configured. Attempting discovery...");
            string targetSlug = string.IsNullOrEmpty(_settings.LutrisId) ? "dark-ages--1" : _settings.LutrisId;

            if (string.IsNullOrEmpty(_settings.LutrisId))
            {
                _settings.LutrisId = targetSlug;
            }

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

        if (!string.IsNullOrEmpty(_settings.GamePath))
        {
            await CheckForFileUpdatesAsync();
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

        // Normalize backslashes to forward slashes for Linux path resolution
        string normalizedPath = relativePath.Replace('\\', Path.DirectorySeparatorChar);
        return Path.Combine(baseDir, normalizedPath);
    }

    private static void CleanupMalformedFiles()
    {
        if (string.IsNullOrEmpty(_settings.GamePath) || !Directory.Exists(_settings.GamePath)) return;

        try
        {
            var malformedFiles = Directory.GetFiles(_settings.GamePath, "*\\*", SearchOption.AllDirectories);
            if (malformedFiles.Length > 0)
            {
                Console.WriteLine($"[Updater] Cleaning up {malformedFiles.Length} malformed files...");
                foreach (var file in malformedFiles)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static async Task CheckForFileUpdatesAsync()
    {
        CleanupMalformedFiles();
        Console.WriteLine("[Updater] Checking for game updates...");
        try
        {
            var fileDetails = await _client.GetFileDetailsAsync(UnoraApiRoutes.FileDetails);
            var filesToUpdate = fileDetails.Where(NeedsUpdate).ToList();

            if (filesToUpdate.Count == 0)
            {
                Console.WriteLine("[Updater] Game is up to date.");
                return;
            }

            Console.WriteLine($"[Updater] Updating {filesToUpdate.Count} files...");
            var totalBytesToDownload = filesToUpdate.Sum(f => f.Size);
            long totalDownloaded = 0;

            foreach (var fileDetail in filesToUpdate)
            {
                var filePath = GetFilePath(fileDetail.RelativePath);
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Console.Write($"[Updater] Downloading {fileDetail.RelativePath} ({FormatBytes(fileDetail.Size)})... ");

                var progress = new Progress<UnoraClient.DownloadProgress>(p =>
                {
                    // For CLI we'll just show completion per file to keep it clean, 
                    // but we could add a progress bar here if needed.
                });

                await _client.DownloadFileAsync(UnoraApiRoutes.GameFile(fileDetail.RelativePath), filePath, progress);
                totalDownloaded += fileDetail.Size;
                Console.WriteLine("Done.");
            }

            Console.WriteLine($"[Updater] Successfully updated {FormatBytes(totalDownloaded)}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Updater] Error checking for updates: {ex.Message}");
        }
    }

    private static bool NeedsUpdate(FileDetail fileDetail)
    {
        var filePath = GetFilePath(fileDetail.RelativePath);
        if (!File.Exists(filePath)) return true;

        try
        {
            string localHash = CalculateHash(filePath);
            return !localHash.Equals(fileDetail.Hash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static string CalculateHash(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        return BitConverter.ToString(md5.ComputeHash(stream));
    }

    private static string FormatBytes(long bytes)
    {
        string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblSByte = bytes;
        for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return $"{dblSByte:0.##} {Suffix[i]}";
    }
}
