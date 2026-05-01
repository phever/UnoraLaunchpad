using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnoraLaunchpad.Definitions;

namespace UnoraLaunchpad;

public sealed class ChaosClientLauncher : IGameLauncher
{
    public Task<Process> LaunchAsync(LaunchContext context)
    {
        var chaosFolder = Path.Combine(context.InstallRoot, CONSTANTS.CHAOS_CLIENT_FOLDER_NAME);
        var exePath     = Path.Combine(chaosFolder, CONSTANTS.CHAOS_CLIENT_EXECUTABLE);

        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = chaosFolder,
            UseShellExecute  = false, // required to set EnvironmentVariables
            CreateNoWindow   = false,
            EnvironmentVariables =
            {
                ["DA_PATH"] = @"..\",
                ["DA_LOBBY_HOST"] = context.LobbyHost,
                ["DA_LOBBY_PORT"] = context.LobbyPort.ToString()
            }
        };

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException(
                          "Process.Start returned null; the OS did not create a new process.");

        return Task.FromResult(process);
    }
}