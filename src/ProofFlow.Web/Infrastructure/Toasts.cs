using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// Messages that survive a redirect.
///
/// A controller that saves something redirects afterwards, so the page that could have shown the
/// confirmation is already gone by the time the browser renders anything. TempData carries the
/// sentence across that gap.
///
/// A list rather than a single slot: one action can produce two things worth saying — "saved" and
/// "the schedule this belonged to was paused because its scenario changed".
/// </summary>
public static class ToastExtensions
{
    private const string Key = "pf:toasts";

    public static void Success(this ITempDataDictionary tempData, string message) => Add(tempData, "success", message);

    public static void Error(this ITempDataDictionary tempData, string message) => Add(tempData, "error", message);

    public static void Warn(this ITempDataDictionary tempData, string message) => Add(tempData, "warn", message);

    public static void Info(this ITempDataDictionary tempData, string message) => Add(tempData, "info", message);

    private static void Add(ITempDataDictionary tempData, string kind, string message)
    {
        var existing = Read(tempData).ToList();
        existing.Add(new Toast(kind, message));
        tempData[Key] = JsonSerializer.Serialize(existing);
    }

    /// <summary>Reads and clears. Called once by the layout, so a message cannot appear twice.</summary>
    public static IReadOnlyList<Toast> Toasts(this ITempDataDictionary tempData)
    {
        var toasts = Read(tempData);
        tempData.Remove(Key);
        return toasts;
    }

    private static IReadOnlyList<Toast> Read(ITempDataDictionary tempData)
    {
        if (tempData.Peek(Key) is not string json || json.Length == 0) return [];

        try
        {
            return JsonSerializer.Deserialize<List<Toast>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public sealed record Toast(string Kind, string Message);
