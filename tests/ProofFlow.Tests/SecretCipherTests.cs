using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProofFlow.Infrastructure.Security;

namespace ProofFlow.Tests;

public class SecretCipherTests
{
    private static AesGcmSecretCipher Cipher(string key = "a-test-master-key-value") =>
        new(Configuration(("ProofFlow:MasterKey", key)), NullLogger<AesGcmSecretCipher>.Instance);

    [Fact]
    public void A_value_round_trips()
    {
        var cipher = Cipher();
        var sealedSecret = cipher.Seal("hunter2-the-actual-token");

        cipher.Open(sealedSecret).Should().Be("hunter2-the-actual-token");
    }

    [Fact]
    public void Persian_text_round_trips()
    {
        // UTF-8 in and out, explicitly: a cipher that mangles non-Latin text fails only for the
        // people whose language it is.
        var cipher = Cipher();
        const string value = "کلید محرمانهٔ سامانه";

        cipher.Open(cipher.Seal(value)).Should().Be(value);
    }

    [Fact]
    public void The_same_value_seals_differently_every_time()
    {
        var cipher = Cipher();

        var first = cipher.Seal("same-value");
        var second = cipher.Seal("same-value");

        // A fresh nonce per value. GCM's failure on nonce reuse is not degraded security, it is
        // catastrophic: two messages under one nonce leak both plaintexts and the auth key.
        first.Nonce.Should().NotBe(second.Nonce);
        first.Ciphertext.Should().NotBe(second.Ciphertext);
    }

    [Fact]
    public void A_tampered_ciphertext_is_refused_rather_than_decrypted_to_something_else()
    {
        var cipher = Cipher();
        var sealedSecret = cipher.Seal("bearer-token-value");

        var bytes = Convert.FromBase64String(sealedSecret.Ciphertext);
        bytes[0] ^= 0xFF;
        var tampered = sealedSecret with { Ciphertext = Convert.ToBase64String(bytes) };

        // This is what GCM buys over CBC. Without the tag, a bearer token that decrypts to a
        // *different* string is a worse outcome than one that fails to decrypt at all.
        var open = () => cipher.Open(tampered);
        open.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_tampered_tag_is_refused()
    {
        var cipher = Cipher();
        var sealedSecret = cipher.Seal("bearer-token-value");

        var tag = Convert.FromBase64String(sealedSecret.Tag);
        tag[0] ^= 0xFF;

        var open = () => cipher.Open(sealedSecret with { Tag = Convert.ToBase64String(tag) });
        open.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Another_installation_cannot_read_this_one_s_secrets()
    {
        var mine = Cipher("my-master-key");
        var theirs = Cipher("their-master-key");

        var open = () => theirs.Open(mine.Seal("value"));
        open.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_failure_message_does_not_say_which_part_was_wrong()
    {
        var mine = Cipher("my-master-key");
        var theirs = Cipher("their-master-key");

        // "Wrong key" versus "altered ciphertext" is useful to someone probing the store and
        // useless to an operator, who needs the same answer either way.
        var open = () => theirs.Open(mine.Seal("value"));
        open.Should().Throw<InvalidOperationException>()
            .WithMessage("*master key changed, or the row was altered*");
    }

    [Fact]
    public void An_empty_value_round_trips()
    {
        var cipher = Cipher();
        cipher.Open(cipher.Seal(string.Empty)).Should().BeEmpty();
    }

    [Fact]
    public void A_generated_key_is_the_same_key_after_a_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pf-key-{Guid.CreateVersion7():N}");
        var path = Path.Combine(directory, "master.key");

        try
        {
            var first = new AesGcmSecretCipher(
                Configuration(("ProofFlow:MasterKeyPath", path)), NullLogger<AesGcmSecretCipher>.Instance);
            var sealedSecret = first.Seal("survives-a-restart");

            // A second instance stands in for the next process start. If the generated key were
            // derived from anything with fresh randomness in it, this is where every stored secret
            // would quietly become unreadable — days later, as an authentication error somewhere else.
            var second = new AesGcmSecretCipher(
                Configuration(("ProofFlow:MasterKeyPath", path)), NullLogger<AesGcmSecretCipher>.Instance);

            second.Open(sealedSecret).Should().Be("survives-a-restart");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
