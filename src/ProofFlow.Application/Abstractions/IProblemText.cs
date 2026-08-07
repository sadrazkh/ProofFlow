using ProofFlow.TestEngine.Nodes;

namespace ProofFlow.Application.Abstractions;

/// <summary>
/// Turns a validator's code into a sentence, in the reader's language.
///
/// A port rather than a call to a localiser, because the engine that produces the codes knows
/// nothing about languages and the layer that knows about languages is the web. This is the seam.
/// </summary>
public interface IProblemText
{
    string For(GraphProblem problem);
}
