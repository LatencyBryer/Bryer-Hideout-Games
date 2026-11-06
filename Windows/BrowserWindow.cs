/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Numerics;
using BryersHideoutGames.Browser;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BryersHideoutGames.Windows;
public sealed class BrowserWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private OffscreenBrowser? content;
    private bool disposed;
    private bool previousTitleBarOnly;
    private SiteDestination? current;
    private BrowserScreenMode? openedScreenMode;
    private bool applyScreenSize;
    private Vector2 lastActualWindowSize;
    public SiteDestination? CurrentGame => current;
    public bool CapturesKeyboard => IsOpen && Collapsed != true && content?.CapturesKeyboard == true;
    public bool IsBrowserCollapsed => Collapsed == true;
    public void RequestKeyboardFocusRelease() => content?.RequestKeyboardFocusRelease();

    public BrowserWindow(Plugin plugin) : base("Bryer's Hideout - Minigame###BryerGamesGame",
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        Size = new Vector2(1180, 870);
        SizeCondition = ImGuiCond.FirstUseEver;
        PositionCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 350),
            MaximumSize = new Vector2(2400, 1700),
        };
        RespectCloseHotkey = true;
    }

    public override void OnOpen()
    {
        // Center only when opening a new mode for the first time.
        var vp = ImGui.GetMainViewport();
        var desired = plugin.Configuration.ScreenMode == BrowserScreenMode.Compact
            ? new Vector2(plugin.Configuration.CompactBrowserWidth, plugin.Configuration.CompactBrowserHeight)
            : new Vector2(plugin.Configuration.DesktopBrowserWidth, plugin.Configuration.DesktopBrowserHeight);
        Position = vp.Pos + Vector2.Max(Vector2.Zero, (vp.Size - desired) / 2f);
    }

    public override void PreDraw()
    {
        if (Collapsed == true) content?.ReleaseFocusFromWindow();
        // Prevent dragging the web surface from moving the Plugin window.
        // Leave normal title-bar dragging and edge resizing enabled.
        previousTitleBarOnly = ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly;
        ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = true;
        if (applyScreenSize)
        {
            // Set the size/position during the UI draw.
            var viewport = ImGui.GetMainViewport();
            var desiredSize = Size ?? (plugin.Configuration.ScreenMode == BrowserScreenMode.Compact
                ? new Vector2(plugin.Configuration.CompactBrowserWidth, plugin.Configuration.CompactBrowserHeight)
                : new Vector2(plugin.Configuration.DesktopBrowserWidth, plugin.Configuration.DesktopBrowserHeight));
            Position = viewport.Pos + Vector2.Max(Vector2.Zero, (viewport.Size - desiredSize) / 2f);
            SizeCondition = ImGuiCond.Always;
            PositionCondition = ImGuiCond.Always;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar();
        ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = previousTitleBarOnly;
        if (applyScreenSize)
        {
            applyScreenSize = false;
            SizeCondition = ImGuiCond.FirstUseEver;
            PositionCondition = ImGuiCond.FirstUseEver;
        }
    }

    private void SaveCurrentWindowSize()
    {
        if (!openedScreenMode.HasValue || lastActualWindowSize.X < 320f || lastActualWindowSize.Y < 320f)
            return;
        var mode = openedScreenMode.Value;
        var width = (int)MathF.Round(lastActualWindowSize.X);
        var height = (int)MathF.Round(lastActualWindowSize.Y);
        var cfg = plugin.Configuration;
        if (mode == BrowserScreenMode.Compact)
        {
            // Keep the compact viewport below tablet breakpoints.
            cfg.CompactBrowserWidth = Math.Clamp(width, 350, 520);
            cfg.CompactBrowserHeight = Math.Clamp(height, 520, 1200);
        }
        else
        {
            cfg.DesktopBrowserWidth = Math.Clamp(width, 480, 2400);
            cfg.DesktopBrowserHeight = Math.Clamp(height, 350, 1700);
        }
        cfg.Save();
    }

    private void PrepareWindowForMode(BrowserScreenMode mode)
    {
        if (mode == BrowserScreenMode.Compact)
        {
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(350, 520),
                MaximumSize = new Vector2(520, 1200),
            };
            Size = new Vector2(Math.Clamp(plugin.Configuration.CompactBrowserWidth, 350, 520),
                Math.Clamp(plugin.Configuration.CompactBrowserHeight, 520, 1200));
        }
        else
        {
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(480, 350),
                MaximumSize = new Vector2(2400, 1700),
            };
            Size = new Vector2(Math.Clamp(plugin.Configuration.DesktopBrowserWidth, 480, 2400),
                Math.Clamp(plugin.Configuration.DesktopBrowserHeight, 350, 1700));
        }
        applyScreenSize = true;
    }

    public void Open(SiteDestination destination)
    {
        if (disposed || !plugin.Account.IsAuthorized) return;
        var requestedMode = plugin.Configuration.ScreenMode;
        if (openedScreenMode != requestedMode)
        {
            // Keep the existing Chromium session alive.
            SaveCurrentWindowSize();
            // On the first Desktop open, preserve the player's existing window
            // size/position; Compact always needs its own portrait size.
            if (openedScreenMode != null || requestedMode == BrowserScreenMode.Compact)
                PrepareWindowForMode(requestedMode);
            openedScreenMode = requestedMode;
            plugin.Log.Information("[BryerGames] Opening {Mode} browser viewport.", requestedMode);
        }
        if (!plugin.Runtime.EnsureStarted()) { IsOpen = true; return; }
        try
        {
            var target = plugin.GetUrl(destination);
            if (content == null)
                content = new OffscreenBrowser(target, destination, plugin.Log, plugin.TextureProvider, plugin.Runtime,
                    plugin.Configuration.BrowserFpsLimit, plugin.Configuration.RenderScalePercent,
                    plugin.Configuration.BrowserProfileId, requestedMode == BrowserScreenMode.Compact);
            else if (current != destination)
                content.Navigate(target);
            current = destination;
            IsOpen = true;
        }
        catch (Exception ex)
        {
            plugin.Log.Error(ex, "[BryerGames] Could not open {Game}.", destination);
        }
    }

    public override void Draw()
    {
        if (plugin.Runtime.Error is { } error && content == null) { ImGui.TextWrapped(error); return; }
        if (content == null) { ImGui.TextUnformatted("Browser starting... Check /xllog if needed."); return; }
        lastActualWindowSize = ImGui.GetWindowSize();
        var size = ImGui.GetContentRegionAvail();
        content.Draw(Math.Max(1, size.X), Math.Max(1, size.Y));
    }

    public void CloseForLogOff()
    {
        SaveCurrentWindowSize();
        content?.Dispose();
        content = null;
        current = null;
        IsOpen = false;
    }

    public override void OnClose() => CloseForLogOff();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CloseForLogOff();
    }
}
