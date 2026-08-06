namespace ProofFlow.Web.ViewModels;

/// <summary>
/// One number on a dashboard.
///
/// <paramref name="Value"/> is a string rather than a number so a tile can honestly say "—" when
/// there is nothing to measure yet. A zero would be a claim: "no failures" reads very differently
/// from "nothing has run".
/// </summary>
public sealed record StatTile(string Label, string Value, string Icon, string Tone, string? Caption = null);
