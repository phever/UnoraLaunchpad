namespace UnoraLaunchpad;

/// <summary>
/// Inputs for an <see cref="IGameLauncher"/> invocation.
/// LobbyHost is a hostname (not a resolved IP); implementations resolve as needed.
/// </summary>
public sealed class LaunchContext
{
    public string InstallRoot      { get; }
    public string LobbyHost        { get; }
    public int    LobbyPort        { get; }
    public bool   SkipIntro        { get; }
    public bool   UseDawndWindower { get; }

    public LaunchContext(
        string installRoot,
        string lobbyHost,
        int    lobbyPort,
        bool   skipIntro,
        bool   useDawndWindower)
    {
        InstallRoot      = installRoot;
        LobbyHost        = lobbyHost;
        LobbyPort        = lobbyPort;
        SkipIntro        = skipIntro;
        UseDawndWindower = useDawndWindower;
    }
}
