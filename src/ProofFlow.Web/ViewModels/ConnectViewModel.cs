using ProofFlow.Web.Controllers;

namespace ProofFlow.Web.ViewModels;

public sealed record ConnectViewModel
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }

    /// <summary>
    /// The address of the fake API this application serves.
    ///
    /// Offered as something to try, not as a default. It needs a token, so somebody with nothing of
    /// their own can still walk all four steps and see what the end looks like — which is a better
    /// answer to «what does this do» than a paragraph.
    /// </summary>
    public required string SampleBaseUrl { get; init; }

    /// <summary>
    /// An environment being changed rather than made, as the four steps would have collected it.
    ///
    /// Null for a first connection. When it is set, the flow is the environment's authentication
    /// editor: the same four questions, already answered, with the third still refusing to save
    /// anything that has not just been proved to work.
    /// </summary>
    public ConnectAttempt? Existing { get; init; }
}
