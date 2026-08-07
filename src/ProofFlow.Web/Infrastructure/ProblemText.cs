using Microsoft.Extensions.Localization;
using ProofFlow.Application.Abstractions;
using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Web.Infrastructure;

/// <summary>
/// The validator's codes, as sentences in the reader's language.
///
/// Every message is written for the person the brief names — somebody who is not a programmer — so
/// it says what to do rather than what rule was broken: "this step has no address to send to", not
/// "required property 'url' missing on node http.request".
///
/// Type names are localised too. A message reading "produces a number and expects a response" only
/// works if "a number" and "a response" are in the same language as the sentence around them.
/// </summary>
public sealed class ProblemText(IStringLocalizer localizer) : IProblemText
{
    public string For(GraphProblem problem)
    {
        var arguments = problem.Arguments
            .Select(argument => (object)Translate(argument))
            .ToArray();

        return localizer[$"graphProblem.{problem.Code}", arguments].Value;
    }

    /// <summary>
    /// Data-type names come through as enum names and become words; everything else is a value a
    /// person typed, and is passed through untouched.
    /// </summary>
    private string Translate(string argument) =>
        Enum.TryParse<DataType>(argument, out _) ? localizer[$"portType.{argument}"].Value : argument;
}
