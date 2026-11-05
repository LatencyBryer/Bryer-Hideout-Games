/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */

using System;
using System.Collections.Generic;

namespace BryersHideoutGames.Browser;

public enum SiteDestination
{
    Mines,
    Plinko,
    Wheel,
    Aviator,
    Nalah,
    BlackPrism,
    Library,
}

public static class Destinations
{
    public static readonly SiteDestination[] All =
    [
        SiteDestination.Mines, SiteDestination.Plinko,
        SiteDestination.Wheel, SiteDestination.Aviator,
        SiteDestination.Nalah,
        SiteDestination.BlackPrism, SiteDestination.Library,
    ];

    private static readonly Dictionary<SiteDestination, (string Label, string Host)> Games = new()
    {
        [SiteDestination.Mines] = ("Mines", "mines.bybryer.com"),
        [SiteDestination.Plinko] = ("Plinko", "plinko.bybryer.com"),
        [SiteDestination.Wheel] = ("Fortune Wheel", "wheel.bybryer.com"),
        [SiteDestination.Aviator] = ("Aviator (Boosted+)", "aviator.bybryer.com"),
        [SiteDestination.Nalah] = ("Nalah, Go Eat!", "nalah.bybryer.com"),
        [SiteDestination.BlackPrism] = ("Black Prism", "blackprism.bybryer.com"),
        [SiteDestination.Library] = ("Whispering Library", "library.bybryer.com"),
    };

    public static int IconIndex(SiteDestination game) => game switch
    {
        SiteDestination.Mines => 0, SiteDestination.Plinko => 1,
        SiteDestination.Wheel => 2, SiteDestination.Aviator => 3,
        SiteDestination.Nalah => 4, SiteDestination.BlackPrism => 6,
        SiteDestination.Library => 7, _ => 0,
    };

    // These games currently require the desktop viewport.
    public static bool IsDesktopOnly(SiteDestination game) =>
        game is SiteDestination.Plinko or SiteDestination.BlackPrism or SiteDestination.Library;

    public static string Label(SiteDestination game) => Games[game].Label;
    public static string Host(SiteDestination game) => Games[game].Host;

    public static Uri CreateUrl(SiteDestination game, string? code = null)
    {
        var host = Host(game);
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length == 0) return new Uri($"https://{host}/");
        if (normalized.Length > 64 || normalized.IndexOfAny(['/', '\\', '?', '#', '\r', '\n']) >= 0)
            throw new ArgumentException("Invalid access code format.");
        return new Uri($"https://{host}/{Uri.EscapeDataString(normalized)}");
    }

    public static bool IsAllowedGameUrl(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        foreach (var game in All)
            if (uri.Host.Equals(Host(game), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
