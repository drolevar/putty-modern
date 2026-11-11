using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>The facts of an SSH connection read from PuTTY's event log lines (no engine needed: just the wording).</summary>
[TestClass]
public sealed class ConnectionFactsTests
{
    [TestMethod]
    public void The_lines_of_an_ssh_login_give_the_key_exchange_the_cipher_the_mac_and_the_fingerprint()
    {
        string[] log =
        [
            "Connecting to 127.0.0.1 port 2222",
            "Server version: SSH-2.0-ssh2js1.5.0",
            "Doing ECDH key exchange with curve Curve25519, using hash SHA-256",
            "Server also has ssh-rsa host key, but we don't know it",
            "Host key fingerprint is:",
            "ssh-ed25519 255 SHA256:AbCdEf0123456789",
            "Initialised AES-256 SDCTR (AES-NI accelerated) outbound encryption",
            "Initialised HMAC-SHA-256 (SHA-NI accelerated) outbound MAC algorithm",
            "Initialised AES-256 SDCTR (AES-NI accelerated) inbound encryption",
        ];
        var facts = new ConnectionFacts();

        bool told = false;
        foreach (string line in log)
        {
            told |= facts.Update(line);
        }

        Assert.IsTrue(told);
        Assert.IsTrue(facts.Any);
        Assert.AreEqual("ECDH key exchange with curve Curve25519, using hash SHA-256", facts.KeyExchange);
        Assert.AreEqual("AES-256 SDCTR (AES-NI accelerated)", facts.Cipher);
        Assert.AreEqual("HMAC-SHA-256 (SHA-NI accelerated)", facts.Mac);
        Assert.AreEqual("ssh-ed25519 255 SHA256:AbCdEf0123456789", facts.HostKey);
        Assert.AreEqual("SHA256:AbCdEf0123456789", facts.Fingerprint);
    }

    [TestMethod]
    public void A_connection_that_is_not_ssh_has_no_facts()
    {
        var facts = new ConnectionFacts();

        foreach (string line in new[] { "Connecting to 10.0.0.1 port 23", "Connected to 10.0.0.1", "Server sent command exit status 0" })
        {
            Assert.IsFalse(facts.Update(line));
        }

        Assert.IsFalse(facts.Any);
        Assert.IsNull(facts.Fingerprint);
    }

    [TestMethod]
    public void A_key_re_exchange_updates_what_was_learned_and_the_fingerprint_line_is_only_read_after_its_introduction()
    {
        var facts = new ConnectionFacts();
        facts.Update("Doing Diffie-Hellman key exchange using 2048-bit modulus and hash SHA-256 with standard group \"group14\" from the server");
        facts.Update("ssh-rsa 2048 SHA256:NotAFingerprintYet");              // not introduced: ignored
        Assert.IsNull(facts.HostKey);

        facts.Update("Host key fingerprint is:");
        facts.Update("ssh-rsa 2048 SHA256:Real");
        facts.Update("Doing ECDH key exchange with curve Curve25519, using hash SHA-256");     // a later key exchange

        StringAssert.Contains(facts.KeyExchange, "Curve25519");
        Assert.AreEqual("SHA256:Real", facts.Fingerprint);
    }
}
