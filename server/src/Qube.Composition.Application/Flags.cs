namespace Qube.Composition.Application;

/// <summary>
/// A flag as written, wherever it is typed (ADR-0006's note 4 of 2026-11-11, ADR-0016 §1): <c>on</c>, <c>yes</c>,
/// <c>true</c> or <c>y</c>; <c>off</c>, <c>no</c>, <c>false</c> or <c>n</c>; in any case. One reader, so a product's
/// term, a cell and a command read it alike. Written back, a flag is <c>on</c> or <c>off</c>.
/// </summary>
public static class FlagWords
{
    /// <summary>The flag a word names, or none where it names neither: never a guess.</summary>
    public static bool? Read(string text) => text.Trim().ToLowerInvariant() switch
    {
        "on" or "yes" or "true" or "y" => true,
        "off" or "no" or "false" or "n" => false,
        _ => null,
    };
}
