/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using Dalamud.Configuration;

namespace BryersHideoutGames;

public enum BrowserScreenMode
{
    Desktop = 0,
    Compact = 1,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 7;
    public int BrowserFpsLimit { get; set; } = 60;
    // Limits FFXIV itself only while a Bryer minigame is open and FFXIV is foreground.
    public int FfxivFpsLimit { get; set; } = 60;
    public int RenderScalePercent { get; set; } = 100;
    public BrowserScreenMode ScreenMode { get; set; } = BrowserScreenMode.Compact;
    // Each screen mode keeps its own last used browser window size.
    public int DesktopBrowserWidth { get; set; } = 1180;
    public int DesktopBrowserHeight { get; set; } = 870;
    public int CompactBrowserWidth { get; set; } = 430;
    public int CompactBrowserHeight { get; set; } = 780;
    public bool LauncherCompactSizeAppliedV16 { get; set; } = false;
    public bool LauncherCompactSizeAppliedV17 { get; set; } = false;
    public bool UseSavedCodeForDirectLinks { get; set; } = false;
    // Encrypted with Windows DPAPI, CurrentUser.
    public string EncryptedAccessCode { get; set; } = string.Empty;
    public string EncryptedSessionToken { get; set; } = string.Empty;
    // Random, non-secret browser profile identifier; rotates after Log Off.
    public string BrowserProfileId { get; set; } = string.Empty;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
