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

    // Dalamud installs custom-repository packages under
    // installedPlugins/<InternalName>/<AssemblyVersion>/. The marker and Chromium
    // helper are distributed beside the plugin DLL, NOT in <InternalName>/.
    // Development builds are the exception: an IDE may load the DLL from bin/.
    private string RuntimeDirectory()
    {
        if (File.Exists(Path.Combine(pluginDirectory, "_browser-runtime-current.txt")))
            return pluginDirectory;

        var directory = new DirectoryInfo(pluginDirectory);
        for (var depth = 0; directory != null && depth < 9; depth++, directory = directory.Parent)
        {
            if (directory.Name.Equals("BryersHideoutGames", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(directory.FullName, "Host", "BryersHideoutGamesHost.csproj")))
                return directory.FullName;
        }

        // Do not search other installed plugin versions or silently use stale DLLs.
        return pluginDirectory;
    }

    // The marker is shipped INSIDE the Dalamud release ZIP, together with the
    // immutable v13 helper. Players never need the developer build script.
    private (string Path, string? Problem) ResolveHost()
    {
        var root = RuntimeDirectory();
        var marker = Path.Combine(root, "_browser-runtime-current.txt");
        const string installAdvice = "Update or reinstall Bryer's Hideout Games through Dalamud. "
            + "If this persists, the GitHub Release package is missing its Chromium runtime.";
        if (!File.Exists(marker))
            return (string.Empty, $"Chromium runtime marker is missing from this installation: {marker}. {installAdvice}");

        string directoryName;
        try { directoryName = File.ReadAllText(marker).Trim(); }
        catch (IOException ex) { return (string.Empty, $"Could not read the Chromium runtime marker: {ex.GetType().Name}. {installAdvice}"); }
        catch (UnauthorizedAccessException) { return (string.Empty, $"Chromium runtime marker is not readable. {installAdvice}"); }

        var expectedPrefix = $"_browser-runtime-v{RuntimeProtocolVersion}-";
        if (!Regex.IsMatch(directoryName, $"^{Regex.Escape(expectedPrefix)}[0-9a-fA-F]{{12}}$", RegexOptions.CultureInvariant))
            return (string.Empty, $"The packaged Chromium runtime marker is invalid or incompatible (expected {expectedPrefix}<12-hex>). {installAdvice}");

        var runtimeDir = Path.Combine(root, directoryName);
        var executable = Path.Combine(runtimeDir, HostName);
        if (!File.Exists(executable))
            return (executable, $"Chromium runtime v{RuntimeProtocolVersion} is missing its host executable: {executable}. {installAdvice}");
        if (!File.Exists(Path.Combine(runtimeDir, "libcef.dll"))
            || !File.Exists(Path.Combine(runtimeDir, "CefSharp.Core.Runtime.dll"))
            || !File.Exists(Path.Combine(runtimeDir, "CefSharp.BrowserSubprocess.exe"))
            || !File.Exists(Path.Combine(runtimeDir, "locales", "en-US.pak")))
            return (executable, $"The packaged Chromium runtime is incomplete: {runtimeDir}. {installAdvice}");

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
