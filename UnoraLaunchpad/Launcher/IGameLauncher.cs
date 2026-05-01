using System.Diagnostics;
using System.Threading.Tasks;

namespace UnoraLaunchpad;

public interface IGameLauncher
{
    /// <summary>
    /// Spawns the game client process and returns it in a running state.
    /// Caller is responsible for disposing the returned <see cref="Process"/> when done.
    /// </summary>
    Task<Process> LaunchAsync(LaunchContext context);
}
