using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UnoraLaunchpad.Linux;

public static class LutrisLauncher
{
    public static Process LaunchGame(string lutrisId)
    {
        Console.WriteLine($"[Lutris] Launching game via Lutris ID: {lutrisId}...");
        var psi = new ProcessStartInfo
        {
            FileName = "lutris",
            Arguments = $"lutris:rungame/{lutrisId}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = psi };
        // We don't read these, effectively discarding the output
        process.Start();
        return process;
    }

    public static Dictionary<string, string> GetLutrisWineEnv(string gameSlug)
    {
        var env = new Dictionary<string, string>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string gamesDir = Path.Combine(home, ".local/share/lutris/games");
        string runnersDir = Path.Combine(home, ".local/share/lutris/runners/wine");

        if (!Directory.Exists(gamesDir)) return env;

        // More flexible search: look for files containing the slug or exactly matching it
        var files = Directory.GetFiles(gamesDir, "*.yml")
            .Where(f => Path.GetFileName(f).Contains(gameSlug) || File.ReadAllText(f).Contains($"game_slug: {gameSlug}"))
            .ToArray();

        if (files.Length == 0) return env;

        string configPath = files[0];
        string content = File.ReadAllText(configPath);

        // Find wine version
        var match = Regex.Match(content, @"wine:\s*\r?\n\s*version:\s*([^\r\n]+)", RegexOptions.Multiline);
        if (!match.Success)
        {
            // Try top-level wine version
            match = Regex.Match(content, @"^wine:[\s\S]*?\n\s*version:\s*([^\n]+)", RegexOptions.Multiline);
        }

        if (match.Success)
        {
            string version = match.Groups[1].Value.Trim('\'', '"', ' ');
            string wineDir = Path.Combine(runnersDir, version);

            if (!Directory.Exists(wineDir))
            {
                // Try to find a directory that starts with this version
                var dirs = Directory.GetDirectories(runnersDir, $"{version}*");
                if (dirs.Length > 0) wineDir = dirs[0];
            }

            string[] binCandidates = new[]
            {
                Path.Combine(wineDir, "files", "bin-wow64"),
                Path.Combine(wineDir, "files", "bin"),
                Path.Combine(wineDir, "bin")
            };

            foreach (var binDir in binCandidates)
            {
                string wineBin = Path.Combine(binDir, "wine");
                if (File.Exists(wineBin))
                {
                    env["WINE"] = wineBin;
                    env["WINELOADER"] = wineBin;

                    string wineserver = Path.Combine(binDir, "wineserver");
                    if (File.Exists(wineserver))
                    {
                        env["WINESERVER"] = wineserver;
                    }

                    // Add wine bin to PATH
                    string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (!currentPath.Contains(binDir))
                    {
                        env["PATH"] = binDir + ":" + currentPath;
                    }

                    // Set LD_LIBRARY_PATH
                    string wineLib = Path.Combine(wineDir, "files", "lib64");
                    if (!Directory.Exists(wineLib)) wineLib = Path.Combine(wineDir, "lib64");
                    if (!Directory.Exists(wineLib)) wineLib = Path.Combine(wineDir, "files", "lib");
                    if (!Directory.Exists(wineLib)) wineLib = Path.Combine(wineDir, "lib");

                    if (Directory.Exists(wineLib))
                    {
                        string currentLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                        env["LD_LIBRARY_PATH"] = wineLib + (string.IsNullOrEmpty(currentLd) ? "" : ":" + currentLd);
                    }

                    break;
                }
            }
        }

        var prefixMatch = Regex.Match(content, @"^game:[\s\S]*?\n\s*prefix:\s*([^\n]+)", RegexOptions.Multiline);
        if (prefixMatch.Success)
        {
            string prefix = prefixMatch.Groups[1].Value.Trim('\'', '"', ' ');
            if (!string.IsNullOrEmpty(prefix)) env["WINEPREFIX"] = prefix;
        }

        var archMatch = Regex.Match(content, @"^game:[\s\S]*?\n\s*arch:\s*([^\n]+)", RegexOptions.Multiline);
        if (archMatch.Success)
        {
            env["WINEARCH"] = archMatch.Groups[1].Value.Trim('\'', '"', ' ');
        }

        // Capture system environment variables from Lutris config
        var envSectionMatch = Regex.Match(content, @"^\s*env:\s*\r?\n([\s\S]*?)(?=\r?\n\s*\w+:|$)", RegexOptions.Multiline);
        if (envSectionMatch.Success)
        {
            var envLines = envSectionMatch.Groups[1].Value.Split('\n');
            foreach (var line in envLines)
            {
                var kvMatch = Regex.Match(line, @"^\s*(\w+):\s*([^\r\n]*)");
                if (kvMatch.Success)
                {
                    string key = kvMatch.Groups[1].Value;
                    string val = kvMatch.Groups[2].Value.Trim('\'', '"', ' ');
                    if (!string.IsNullOrWhiteSpace(key) && !env.ContainsKey(key)) env[key] = val;
                }
            }
        }

        return env;
    }

    public static void SetupWineHostsRedirect(string winePrefix, string hostname, string ip)
    {
        if (string.IsNullOrEmpty(winePrefix)) return;

        string hostsPath = Path.Combine(winePrefix, "drive_c", "windows", "system32", "drivers", "etc", "hosts");
        if (!File.Exists(hostsPath)) return;

        try
        {
            string content = File.ReadAllText(hostsPath);
            string entry = $"{ip}  {hostname}";
            if (content.Contains(hostname) && content.Contains(ip)) return;
            File.AppendAllText(hostsPath, Environment.NewLine + entry + Environment.NewLine);
            Console.WriteLine($"[Launcher] Added Wine hosts redirect: {hostname} -> {ip}");
        }
        catch { }
    }

    public static void ApplyRegistryFix(string winePrefix, string wineCmd, Dictionary<string, string> env, string exePath = null)
    {
        if (string.IsNullOrEmpty(winePrefix) || !Directory.Exists(winePrefix)) return;

        var searchDirs = new List<string> { winePrefix };
        if (!string.IsNullOrEmpty(exePath))
        {
            var exeDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir))
            {
                searchDirs.Add(exeDir);
            }
        }

        foreach (var dir in searchDirs)
        {
            // Check for any .reg files that might be our fix
            string[] regFiles = Directory.GetFiles(dir, "*.reg");
            foreach (var regFile in regFiles)
            {
                string fileName = Path.GetFileName(regFile).ToLower();
                if (fileName == "system.reg" || fileName == "user.reg" || fileName == "userdef.reg") continue;

                Console.WriteLine($"[Launcher] Applying registry fix: {regFile}");
                var regPsi = new ProcessStartInfo
                {
                    FileName = wineCmd,
                    Arguments = $"regedit /s \"{regFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var kvp in env) regPsi.EnvironmentVariables[kvp.Key] = kvp.Value;
                if (!regPsi.EnvironmentVariables.ContainsKey("WINEPREFIX")) regPsi.EnvironmentVariables["WINEPREFIX"] = winePrefix;

                try
                {
                    Process.Start(regPsi)?.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Launcher] Failed to apply registry fix {regFile}: {ex.Message}");
                }
            }
        }
    }

    public static Process LaunchDirect(string exePath, string prefixPath, string lutrisId = null, bool useWindower = false)
    {
        Console.WriteLine($"[Launcher] Launching directly via Wine...");
        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(lutrisId)) env = GetLutrisWineEnv(lutrisId);
        if (!string.IsNullOrEmpty(prefixPath) && !env.ContainsKey("WINEPREFIX")) env["WINEPREFIX"] = prefixPath;

        string wineCmd = env.ContainsKey("WINE") ? env["WINE"] : "wine";

        // Apply any registry fixes found in the prefix or game directory
        ApplyRegistryFix(env.GetValueOrDefault("WINEPREFIX"), wineCmd, env, exePath);

        if (useWindower)
        {
            Console.WriteLine($"[Launcher] Resetting Wine (prefix: {env.GetValueOrDefault("WINEPREFIX")})");
            try
            {
                var killPsi = new ProcessStartInfo { FileName = "wineserver", Arguments = "-k", UseShellExecute = false, CreateNoWindow = true };
                foreach (var kvp in env) killPsi.EnvironmentVariables[kvp.Key] = kvp.Value;
                Process.Start(killPsi)?.WaitForExit(2000);
            }
            catch { }

            // Fix renderer and force DLL override in registry
            try
            {
                var regPsi = new ProcessStartInfo { FileName = wineCmd, Arguments = @"reg add ""HKEY_CURRENT_USER\Software\Wine\Direct3D"" /v renderer /t REG_SZ /d gdi /f", UseShellExecute = false, CreateNoWindow = true };
                foreach (var kvp in env) regPsi.EnvironmentVariables[kvp.Key] = kvp.Value;
                Process.Start(regPsi)?.WaitForExit(2000);

                var dllPsi = new ProcessStartInfo { FileName = wineCmd, Arguments = @"reg add ""HKEY_CURRENT_USER\Software\Wine\DllOverrides"" /v ddraw /t REG_SZ /d native,builtin /f", UseShellExecute = false, CreateNoWindow = true };
                foreach (var kvp in env) dllPsi.EnvironmentVariables[kvp.Key] = kvp.Value;
                Process.Start(dllPsi)?.WaitForExit(2000);

                var verPsi = new ProcessStartInfo { FileName = wineCmd, Arguments = @"winecfg /v win7", UseShellExecute = false, CreateNoWindow = true };
                foreach (var kvp in env) verPsi.EnvironmentVariables[kvp.Key] = kvp.Value;
                Process.Start(verPsi)?.WaitForExit(5000);
            }
            catch { }

            env["WINEDLLOVERRIDES"] = "ddraw=n,b";
        }

        // Suppress Fixme/Stub noise to see real errors
        env["WINEDEBUG"] = "-all";

        Console.WriteLine($"[Launcher] Launching {exePath}...");
        var psi = new ProcessStartInfo
        {
            FileName = wineCmd,
            Arguments = $"\"{exePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var kvp in env) psi.EnvironmentVariables[kvp.Key] = kvp.Value;

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    public static async Task<Process> PollForProcessAsync(string processName, CancellationToken ct)
    {
        var possibleNames = new[] { processName, processName.ToLower(), processName.EndsWith(".exe") ? processName[..^4] : processName + ".exe" }.Distinct();
        while (!ct.IsCancellationRequested)
        {
            foreach (var name in possibleNames)
            {
                var process = Process.GetProcessesByName(name).FirstOrDefault();
                if (process != null) return process;
            }
            await Task.Delay(10, ct);
        }
        return null;
    }
}
