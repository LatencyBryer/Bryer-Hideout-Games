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
    private bool resolutionLogged;

    public ChromiumRuntime(IPluginLog log, string pluginDirectory, string configDirectory)
    {
        this.log = log;
        this.pluginDirectory = Path.GetFullPath(pluginDirectory);
        this.configDirectory = configDirectory;
    }

    public string ProfileRoot => Path.Combine(configDirectory, "ChromiumProfile");
    public string? Error { get; private set; }

    // An installed Dalamud plugin may run from .../BryersHideoutGames/1.0.0.X/.
    // The runtime bundled with that DLL must take priority. The previous
    // resolver climbed straight to the BryersHideoutGames parent directory,
    // potentially launching a helper from an earlier installation instead.
    private string? FindLegacyRoot()
    {
        var current = new DirectoryInfo(pluginDirectory);
        for (var depth = 0; current != null && depth < 9; depth++, current = current.Parent)
            if (current.Name.Equals("BryersHideoutGames", StringComparison.OrdinalIgnoreCase))
                return current.FullName;
        return null;
    }

    private (string Path, string? Problem) ResolveHost()
    {
        var root = pluginDirectory;
        var marker = Path.Combine(root, "_browser-runtime-current.txt");
        if (!File.Exists(marker))
        {
            // Never silently load the old root-level Chromium if a versioned
            // plugin DLL is present here: otherwise new plugin code can run
            // with an old helper and misleading performance diagnostics.
            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(pluginDirectory));
            if (Regex.IsMatch(directoryName, @"^\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant) &&
                File.Exists(Path.Combine(pluginDirectory, "BryersHideoutGames.dll")))
            {
                return (string.Empty, $"The Chromium runtime is missing beside the installed plugin DLL: {marker}. Reinstall the full GitHub Release ZIP (including the _browser-runtime-* directory), not the source-code ZIP.");
            }

            // Compatibility for older development layouts where the loader
            // lives in a child directory and the assets live in the root.
            var legacyRoot = FindLegacyRoot();
            if (legacyRoot != null)
            {
                root = legacyRoot;
                marker = Path.Combine(root, "_browser-runtime-current.txt");
            }
        }

        if (!File.Exists(marker))
            return (string.Empty, $"Chromium runtime marker is missing: {marker}. Build and install the complete release package.");

        string runtimeName;
        try { runtimeName = File.ReadAllText(marker).Trim(); }
        catch (IOException ex) { return (string.Empty, $"Could not read the Chromium runtime marker: {ex.GetType().Name}."); }
        catch (UnauthorizedAccessException) { return (string.Empty, "Chromium runtime marker is not readable."); }

        var expectedPrefix = $"_browser-runtime-v{RuntimeProtocolVersion}-";
        if (!Regex.IsMatch(runtimeName, $"^{Regex.Escape(expectedPrefix)}[0-9a-fA-F]{{12}}$", RegexOptions.CultureInvariant))
            return (string.Empty, $"Chromium runtime marker points to an incompatible version: {runtimeName}. Reinstall the full Release ZIP.");

        var runtimeDir = Path.Combine(root, runtimeName);
        var executable = Path.Combine(runtimeDir, HostName);
        if (!File.Exists(executable))
            return (executable, $"Chromium host is missing: {executable}. Reinstall the full Release ZIP.");
        if (!File.Exists(Path.Combine(runtimeDir, "locales", "en-US.pak")))
            return (executable, $"Chromium runtime is incomplete (locales/en-US.pak missing in {runtimeDir}).");

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
        if (!resolutionLogged)
        {
            resolutionLogged = true;
            // Record selection where the existing browser diagnostics are kept,
            // since /xllog can filter informational messages.
            try
            {
                Directory.CreateDirectory(ProfileRoot);
                File.AppendAllText(Path.Combine(ProfileRoot, "runtime-selection.log"),
                    $"{DateTimeOffset.UtcNow:O} PluginDirectory={pluginDirectory} HostExecutable={file}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            log.Information("[BryerGames] Chromium runtime selected: {Executable}", file);
        }
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
