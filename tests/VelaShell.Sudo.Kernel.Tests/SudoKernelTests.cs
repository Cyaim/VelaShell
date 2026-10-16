using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Sudo.Kernel.Tests;

[TestClass]
public sealed class SudoKernelTests
{
    [TestMethod]
    public void LatestControlledCommandUsesExplicitRootAndFailClosedPreparation()
    {
        string wrapped = SudoCommandExecutor.BuildCommand("printf '%s' '漢🙂'; id | cat > /root/file", "READY", "AUTH", "START");
        Assert.Contains("stty raw -echo || exit 125", wrapped);
        Assert.Contains("modes=\" $(stty -a) \" || exit 125", wrapped);
        Assert.Contains("exec sudo -S -u root -p 'AUTH' -- /bin/sh -c ", wrapped);
        Assert.Contains("</dev/null", wrapped);
        Assert.Contains("exit 125; exec /bin/sh -c", wrapped);
        Assert.DoesNotContain("sudo -v", wrapped);
        Assert.AreEqual("'a'\"'\"'b'", SudoCommandExecutor.Quote("a'b"));
    }

    [TestMethod]
    public void Utf8SecretIsClearedAndControlCharactersAreNotAccepted()
    {
        SudoPassword secret = new("密碼é🙂".AsSpan());
        ReadOnlyMemory<byte> bytes = secret.Utf8;
        Assert.AreEqual("密碼é🙂", Encoding.UTF8.GetString(bytes.Span));
        secret.Dispose();
        secret.Dispose();
        Assert.IsLessThan(0, bytes.Span.IndexOfAnyExcept((byte)0));
        foreach (char control in new[] { '\0', '\r', '\n', '\b', '\u001b', '\u007f' })
        {
            Assert.ThrowsExactly<ArgumentException>(() => new SudoPassword(("secret" + control).AsSpan()));
        }
    }

    [TestMethod]
    public void UnknownIsSafeDefaultOutcome()
    {
        Assert.AreEqual(SudoExecutionOutcome.Unknown, Enum.GetValues<SudoExecutionOutcome>()[0]);
    }
}
