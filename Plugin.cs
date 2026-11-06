/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using BryersHideoutGames.Browser;
using BryersHideoutGames.Security;
using BryersHideoutGames.Services;
using BryersHideoutGames.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BryersHideoutGames;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureService { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;

    private readonly WindowSystem windows = new("BryersHideoutGames");
    private readonly GameLauncherWindow launcher;
    private readonly BrowserWindow browser;
    private readonly List<string> registeredCommands = [];
    private DateTime lastDrawErrorUtc = DateTime.MinValue;
    // FFXIV FPS budget while a minigame is visible and the game has foreground focus.
    // Defaults to 60 FPS and follows the saved launcher slider setting.
    private int gameFpsBudget;
    private long lastBudgetTick;
    private bool disposed;

    public Configuration Configuration { get; }
    public AccountService Account { get; }
    public ChromiumRuntime Runtime { get; }
    public IPluginLog Log => PluginLog;
    public ITextureProvider TextureProvider => TextureService;
    public SiteDestination? ActiveGame => browser.IsOpen ? browser.CurrentGame : null;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // New installations start at 60 FPS / 100% quality / Desktop.
        // Existing saved FPS/quality choices remain unchanged after.
        Configuration.BrowserFpsLimit = Math.Clamp(Configuration.BrowserFpsLimit, 30, 60);
        Configuration.RenderScalePercent = Math.Clamp(Configuration.RenderScalePercent, 55, 100);
        if (!Enum.IsDefined(Configuration.ScreenMode)) Configuration.ScreenMode = BrowserScreenMode.Compact;
        if (Configuration.Version < 6)
        {
            if (Configuration.Version < 4 && Configuration.RenderScalePercent == 85)
                Configuration.RenderScalePercent = 100;
            Configuration.ScreenMode = BrowserScreenMode.Compact;
            Configuration.Version = 6;
            Configuration.Save();
        }
        // Add the FFXIV FPS setting without changing any existing browser FPS / quality choices.
        if (Configuration.Version < 7)
        {
            Configuration.FfxivFpsLimit = 60;
            Configuration.Version = 7;
            Configuration.Save();
        }
        var savedGameLimit = Math.Clamp(Configuration.FfxivFpsLimit, 30, 60);
        if (Configuration.FfxivFpsLimit != savedGameLimit)
        {
            Configuration.FfxivFpsLimit = savedGameLimit;
            Configuration.Save();
        }
        gameFpsBudget = savedGameLimit;
        var moduleDir = PluginInterface.AssemblyLocation.DirectoryName
            ?? throw new InvalidOperationException("Dalamud did not provide the plugin install directory.");
        if (moduleDir.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            PluginLog.Warning("[BryerGames] Dalamud loaded a DLL from a bin/ build folder. Point Dev Plugin Locations at the installed root DLL to avoid stale versions.");
        var configDir = PluginInterface.GetPluginConfigDirectory();
        Runtime = new ChromiumRuntime(PluginLog, moduleDir, configDir);
        Account = new AccountService(Configuration, PluginLog);

        launcher = new GameLauncherWindow(this);
        browser = new BrowserWindow(this);
        windows.AddWindow(launcher);
        windows.AddWindow(browser);

        RegisterAction("/bryergames", OpenLauncher, "Open Bryer's Hideout game launcher.");
        RegisterAction("/mines", () => OpenGame(SiteDestination.Mines), "Open Mines.");
        RegisterAction("/plinko", () => OpenGame(SiteDestination.Plinko), "Open Plinko.");
        RegisterAction("/aviator", () => OpenGame(SiteDestination.Aviator), "Open Aviator (Boosted+).");
        RegisterAction("/wheel", () => OpenGame(SiteDestination.Wheel), "Open Fortune Wheel.");
        RegisterAction("/nalah", () => OpenGame(SiteDestination.Nalah), "Open Nalah, Go Eat!.");
        RegisterAction("/blackprism", () => OpenGame(SiteDestination.BlackPrism), "Open Black Prism.");
        RegisterAction("/whisperinglibrary", () => OpenGame(SiteDestination.Library), "Open Whispering Library.");

        Framework.Update += OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw += DrawSafely;
        PluginInterface.UiBuilder.OpenMainUi += OpenLauncher;
        PluginInterface.UiBuilder.OpenConfigUi += OpenLauncher;
        PluginLog.Information("[BryerGames] v{Version} loaded (IPC v13) from {Directory}. Commands registered {Count}: {Names}",
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown", moduleDir,
            registeredCommands.Count, string.Join(", ", registeredCommands));
    }

    public Uri GetUrl(SiteDestination destination)
    {
        if (!Account.IsAuthorized || !Configuration.UseSavedCodeForDirectLinks)
            throw new InvalidOperationException("A verified access code is required.");
        var code = ProtectedAccessCode.Decrypt(Configuration.EncryptedAccessCode);
        if (!AccountService.IsValidCodeSyntax(code)) throw new InvalidOperationException("Saved access code has an invalid format.");
        return Destinations.CreateUrl(destination, code);
    }

    public void OpenGame(SiteDestination game)
    {
        // Also enforce the mode for slash commands, not only icon clicks.
        if (Configuration.ScreenMode == BrowserScreenMode.Compact && Destinations.IsDesktopOnly(game))
        {
            PluginLog.Information("[BryerGames] {Game} is available only in Desktop screen mode.", game);
            return;
        }
        if (!Account.IsAuthorized)
        {
            launcher.RequireCode();
            return;
        }
        try { browser.Open(game); }
        catch (Exception ex) { PluginLog.Error(ex, "[BryerGames] Could not open {Game}.", game); }
    }

    public void LogOff()
    {
        // Must not retain the old player's session.
        var previousProfileId = Configuration.BrowserProfileId;
        browser.CloseForLogOff();
        Account.LogOff();
        Runtime.ForgetProfile(previousProfileId);
    }

    private void OpenLauncher() => launcher.IsOpen = true;

    private void RegisterAction(string name, Action action, string help)
    {
        try
        {
            if (CommandManager.AddHandler(name, new CommandInfo((_, _) =>
            {
                try { action(); }
                catch (Exception ex) { PluginLog.Error(ex, "[BryerGames] Command failed: {Command}.", name); }
            }) { HelpMessage = help })) registeredCommands.Add(name);
            else PluginLog.Warning("[BryerGames] Command {Command} is owned by another plugin.", name);
        }
        catch (Exception ex) { PluginLog.Warning(ex, "[BryerGames] Could not register {Command}.", name); }
    }

    // Apply launcher slider changes immediately and persist for the next session.
    // The FPS limit is controlled exclusively by the saved launcher slider.
    public void SetFfxivFpsLimit(int requested)
    {
        var limit = Math.Clamp(requested, 30, 60);
        if (Configuration.FfxivFpsLimit != limit)
        {
            Configuration.FfxivFpsLimit = limit;
            Configuration.Save();
        }
        gameFpsBudget = limit;
        lastBudgetTick = 0;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed) return;
        // Reuse the existing framework callback. No foreground polling task,
        // thread-priority changes or Chromium flags are introduced here.
        var foreground = IsThisGameForeground();
        if (browser.IsOpen && (!foreground || browser.IsBrowserCollapsed))
            browser.RequestKeyboardFocusRelease();
        else if (foreground && browser.CapturesKeyboard)
            KeyState.ClearAll();

        var limit = gameFpsBudget;
        if (limit == 0 || !browser.IsOpen || !foreground)
        {
            lastBudgetTick = 0;
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var previous = lastBudgetTick;
        if (previous != 0)
        {
            var remaining = (double)Stopwatch.Frequency / limit - (now - previous);
            if (remaining > Stopwatch.Frequency * 0.0015)
            {
                // Coarse sleep only. Never spin-wait on the FFXIV framework thread.
                // Limit the maximum delay to prevent long stalls after Alt+Tab.
                var milliseconds = Math.Min(20, (int)Math.Floor(remaining * 1000d / Stopwatch.Frequency - 0.5));
                if (milliseconds > 0) Thread.Sleep(milliseconds);
            }
        }
        lastBudgetTick = Stopwatch.GetTimestamp();
    }

    private static bool IsThisGameForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private void DrawSafely()
    {
        try
        {
            Account.Tick(launcher.IsOpen, browser.IsOpen);
            windows.Draw();
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - lastDrawErrorUtc < TimeSpan.FromSeconds(8)) return;
            lastDrawErrorUtc = DateTime.UtcNow;
            PluginLog.Error(ex, "[BryerGames] UI failed (rate-limited).");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Framework.Update -= OnFrameworkUpdate;
        gameFpsBudget = 0;
        lastBudgetTick = 0;
        PluginInterface.UiBuilder.Draw -= DrawSafely;
        PluginInterface.UiBuilder.OpenMainUi -= OpenLauncher;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenLauncher;
        foreach (var command in registeredCommands)
        {
            try { CommandManager.RemoveHandler(command); }
            catch { }
        }
        launcher.Dispose();
        browser.Dispose();
        Account.Dispose();
        Runtime.Shutdown();
    }
}
