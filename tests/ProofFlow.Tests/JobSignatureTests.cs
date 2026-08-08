using System.Security.Cryptography;
using FluentAssertions;
using ProofFlow.Contracts.Runners;
using ProofFlow.Infrastructure.Runners;

namespace ProofFlow.Tests;

/// <summary>
/// The proof an agent has that its work came from this installation.
///
/// An agent runs arbitrary HTTP requests against machines inside a private network, which makes
/// "where did this instruction come from" the most important question it ever asks. TLS says the
/// connection was not eavesdropped. It does not say the instruction was not altered by whatever
/// proxy, gateway or sidecar sits in the path, and on the kind of network a runner lives on there
/// are usually several.
/// </summary>
public class JobSignatureTests
{
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void A_job_signed_with_a_key_verifies_with_that_key()
    {
        var key = Key();
        var payload = """{"scenarioId":"019f...","environment":"staging"}""";

        JobSignature.Verify(payload, JobSignature.Sign(payload, key), key).Should().BeTrue();
    }

    [Fact]
    public void One_altered_character_is_a_different_job()
    {
        var key = Key();
        var payload = """{"scenarioId":"a","environment":"staging"}""";

        var signature = JobSignature.Sign(payload, key);

        // The whole point. Somebody who can rewrite the body in flight cannot make the agent run
        // their scenario against their environment.
        JobSignature.Verify(payload.Replace("staging", "produc"), signature, key).Should().BeFalse();
    }

    [Fact]
    public void Another_runners_key_does_not_open_it()
    {
        // Why the key is per runner rather than per installation: one agent cannot verify — or
        // forge — another's work, and losing one key means re-enrolling one machine.
        var payload = """{"scenarioId":"a"}""";

        JobSignature.Verify(payload, JobSignature.Sign(payload, Key()), Key()).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all !!")]
    [InlineData("YWJj")]
    public void A_signature_that_is_missing_or_nonsense_is_refused_rather_than_thrown(string signature)
    {
        // An agent that crashes on a malformed signature is an agent somebody can stop by sending
        // it rubbish.
        var act = () => JobSignature.Verify("""{"a":1}""", signature, Key());

        act.Should().NotThrow();
        JobSignature.Verify("""{"a":1}""", signature, Key()).Should().BeFalse();
    }

    [Fact]
    public void The_same_payload_and_key_always_produce_the_same_signature()
    {
        // Deterministic, which is what lets the server sign and the agent verify independently
        // without exchanging anything but the key.
        var key = Key();
        var payload = """{"scenarioId":"a"}""";

        JobSignature.Sign(payload, key).Should().Be(JobSignature.Sign(payload, key));
    }

    [Fact]
    public void What_is_signed_is_the_text_not_an_object()
    {
        // Two JSON serialisers that differ in key order or whitespace produce two different
        // signatures for the same job, and the bug that follows only appears after somebody
        // upgrades a library. Signing the bytes as written settles it.
        var key = Key();

        var one = """{"a":1,"b":2}""";
        var other = """{"b":2,"a":1}""";

        JobSignature.Sign(one, key).Should().NotBe(JobSignature.Sign(other, key));
    }
}
