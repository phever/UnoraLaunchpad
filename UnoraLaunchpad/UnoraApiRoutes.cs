namespace UnoraLaunchpad;

using Definitions;

public static class UnoraApiRoutes
{
    private static string Base => CONSTANTS.BASE_API_URL.TrimEnd('/');

    public static string FileDetails    => $"{Base}/Unora/{CONSTANTS.GET_FILE_DETAILS_RESOURCE}";
    public static string GameFile(string relativePath) =>
        $"{Base}/Unora/{CONSTANTS.GET_FILE_RESOURCE}{relativePath}";
    public static string GameUpdates    => $"{Base}/Unora/{CONSTANTS.GET_GAME_UPDATES_RESOURCE}";
    public static string LauncherVersion => $"{Base}/{CONSTANTS.GET_LAUNCHER_VERSION_RESOURCE}";
    public static string LauncherExe    => $"{Base}/getlauncher";
}
