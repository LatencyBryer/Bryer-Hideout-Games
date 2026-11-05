/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace BryersHideoutGames.Browser;

/// <summary>Finds a versioned standalone Chromium host. CefSharp never loads into Dalamud.</summary>
public sealed class ChromiumRuntime
{
    private const string HostName = "BryersHideoutGamesHost.exe";
    private const int RuntimeProtocolVersion = 13;
    private readonly IPluginLog log;
    private readonly string pluginDirectory;
    private readonly string configDirectory;
    private bool missingHostLogged;

    public ChromiumRuntime(IPluginLog log, string pluginDirectory, string configDirectory)
    {
        this.log = log;
        this.pluginDirectory = Path.GetFullPath(pluginDirectory);
        this.configDirectory = configDirectory;
    }

    public string ProfileRoot => Path.Combine(configDirectory, "ChromiumProfile");
    public string? Error { get; private set; }

    private string InstallDirectory()
    {
        var current = new DirectoryInfo(pluginDirectory);
        for (var depth = 0; current != null && depth < 9; depth++, current = current.Parent)
            if (current.Name.Equals("BryersHideoutGames", StringComparison.OrdinalIgnoreCase)) return current.FullName;
        return pluginDirectory;
    }

    // The installer writes the exact immutable v13 runtime folder to this marker.
    // Never fall back to a different protocol version: frame formats must match.
    private (string Path, string? Problem) ResolveHost()
    {
        var root = InstallDirectory();
        var marker = Path.Combine(root, "_browser-runtime-current.txt");
        if (!File.Exists(marker))
            return (string.Empty, $"Chromium runtime marker is missing: {marker}. Run build-devplugin.ps1 without -SkipInstall.");

        string directoryName;
        try { directoryName = File.ReadAllText(marker).Trim(); }
        catch (IOException ex) { return (string.Empty, $"Could not read the Chromium runtime marker: {ex.GetType().Name}."); }
        catch (UnauthorizedAccessException) { return (string.Empty, "Chromium runtime marker is not readable."); }

        var expectedPrefix = $"_browser-runtime-v{RuntimeProtocolVersion}-";
        if (!Regex.IsMatch(directoryName, $"^{Regex.Escape(expectedPrefix)}[0-9a-fA-F]{{12}}$", RegexOptions.CultureInvariant))
            return (string.Empty, $"Chromium runtime marker points to an incompatible or invalid version. Expected {expectedPrefix}<12-hex>, got '{directoryName}'. Run build-devplugin.ps1 again.");

        var runtimeDir = Path.Combine(root, directoryName);
        var executable = Path.Combine(runtimeDir, HostName);
        if (!File.Exists(executable))
            return (executable, $"Chromium runtime v{RuntimeProtocolVersion} is not installed: {executable}. Run build-devplugin.ps1 without -SkipInstall.");
        if (!File.Exists(Path.Combine(runtimeDir, "locales", "en-US.pak")))
            return (executable, $"Chromium runtime is incomplete (locales/en-US.pak missing in {runtimeDir}). Run build-devplugin.ps1 to repair it.");

        return (executable, null);
    }

    public string HostExecutable => ResolveHost().Path;
    public bool IsReady => Error == null && ResolveHost().Problem == null;

    public bool EnsureStarted()
    {
        var (file, problem) = ResolveHost();
        if (problem != null)
        {
            Error = problem;
            if (!missingHostLogged)
            {
                missingHostLogged = true;
                log.Error("[BryerGames] Chromium runtime v{Version} unavailable: {Reason}. Dalamud DLL directory: {Directory}. Expected EXE: {Path}",
                    RuntimeProtocolVersion, problem, pluginDirectory, file);
            }
            return false;
        }
        Error = null;
        missingHostLogged = false;
        return true;
    }

    public void ForgetProfile(string profileId)
    {
        if (!Regex.IsMatch(profileId ?? string.Empty, "^[0-9a-fA-F]{32}$")) return;
        var directory = Path.Combine(ProfileRoot, "Games", profileId);
        _ = Task.Run(async () =>
        {
            // CefSharp releases its profile files after the helper exits.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(1200 + attempt * 700);
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            log.Warning("[BryerGames] Old Chromium session files are still in use after Log Off. Close the game window and remove the old profile manually if needed.");
        });
    }

    public void Shutdown() { /* Individual game windows own their helper process. */ }
}
