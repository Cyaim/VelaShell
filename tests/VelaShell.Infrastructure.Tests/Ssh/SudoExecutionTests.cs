using System.IO.Pipelines;
using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

[TestClass]
public sealed class SudoExecutionTests
{
    [TestMethod]
    public void PasswordSupportsUtf8AndClearsOwnedBuffer()
    {
        SudoPassword password = new("密碼é🙂".AsSpan());
        ReadOnlyMemory<byte> memory = password.Utf8;
        Assert.AreEqual("密碼é🙂", Encoding.UTF8.GetString(memory.Span));
        password.Dispose();
        password.Dispose();
        Assert.IsLessThan(0, memory.Span.IndexOfAnyExcept((byte)0));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = password.Utf8);
    }

    [TestMethod]
    public void PasswordRejectsTerminalControlCharactersWithoutLeakingThem()
    {
        foreach (string value in new[] { "secret\n", "secret\r", "secret\0", "secret\b", "secret\u001b" })
        {
            ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => new SudoPassword(value.AsSpan()));
            Assert.DoesNotContain("secret", error.Message);
        }
    }

    [TestMethod]
    public void ControlledCommandQuotesFullRootCommandAndHasClosedStdin()
    {
        string command = "printf '%s' 'a b'; id | cat > /tmp/file";
        string wrapped = SudoCommandExecutor.BuildCommand(command, "READY", "AUTH", "START");
        Assert.Contains("stty raw -echo || exit 125", wrapped);
        Assert.Contains("exec sudo -S -u root -p 'AUTH' -- /bin/sh -c", wrapped);
        Assert.Contains("</dev/null", wrapped);
        Assert.DoesNotContain("sudo -v", wrapped);
        Assert.AreEqual("'a'\"'\"'b'", SudoCommandExecutor.Quote("a'b"));
    }

    [TestMethod]
    public async Task OrdinaryOutputIsBoundedHandlesCarriageReturnAndSplitUtf8()
    {
        Pipe pipe = new();
        List<string> lines = [];
        Task<long> reading = VelaSshClientWrapper.PumpLinesAsync(pipe.Reader, false, (_, text) => lines.Add(text), CancellationToken.None);
        byte[] bytes = Encoding.UTF8.GetBytes(new string('a', 20000) + "漢🙂\rprogress\r\nlast");
        for (int offset = 0; offset < bytes.Length; offset += 113)
        {
            await pipe.Writer.WriteAsync(bytes.AsMemory(offset, Math.Min(113, bytes.Length - offset)));
        }
        await pipe.Writer.CompleteAsync();
        long count = await reading;
        Assert.AreEqual(lines.Count, count);
        Assert.IsTrue(lines.All(line => line.Length <= 4128));
        Assert.Contains("progress", lines);
        Assert.AreEqual("last", lines[^1]);
        string joined = string.Concat(lines).Replace(" [VelaShell: continued]", "", StringComparison.Ordinal);
        Assert.Contains("漢🙂", joined);
        Assert.DoesNotContain("�", joined);
        await pipe.Reader.CompleteAsync();
    }
}
