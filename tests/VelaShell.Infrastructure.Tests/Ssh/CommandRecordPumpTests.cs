using System.IO.Pipelines;
using System.Text;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// <see cref="VelaSshClientWrapper.PumpRecordsAsync" />:按分隔字节切原始字节,不解码、不认 \r;
/// 记录跨分段也拼得起来,最后一条没有分隔符照样交出,超长的整条丢掉。
/// </summary>
[TestClass]
public sealed class CommandRecordPumpTests
{
    [TestMethod]
    public async Task SplitsOnTheSeparatorOnly_KeepingCarriageReturnsAndRawBytes()
    {
        byte[] gbk = [0xD6, 0xD0, 0xCE, 0xC4];
        List<byte[]> records = await PumpAsync([
            [.. "f\t/a\r"u8.ToArray(), (byte)'\n'],
            [.. "d\t/b"u8.ToArray(), 0, .. "c"u8.ToArray(), (byte)'\n', (byte)'f'],
            [.. "\t/"u8.ToArray(), .. gbk, (byte)'\n', .. "tail"u8.ToArray()],
        ]);

        Assert.HasCount(4, records);
        Assert.AreEqual("f\t/a\r", Encoding.ASCII.GetString(records[0]), "\\r 是内容,不是行尾");
        Assert.AreSequenceEqual(new byte[] { (byte)'d', (byte)'\t', (byte)'/', (byte)'b', 0, (byte)'c' }, records[1]);
        byte[] expected = [(byte)'f', (byte)'\t', (byte)'/', .. gbk];
        Assert.AreSequenceEqual(expected, records[2], "跨分段的记录拼得起来,字节原样");
        Assert.AreEqual("tail", Encoding.ASCII.GetString(records[3]), "最后一条没有分隔符也交出");
    }

    [TestMethod]
    public async Task ARecordLongerThanTheCap_IsDroppedWhole()
    {
        byte[] huge = [.. Enumerable.Repeat((byte)'x', VelaSshClientWrapper.MaxRecordBytes + 10)];
        List<byte[]> records = await PumpAsync([
            "ok\n"u8.ToArray(),
            huge[..(huge.Length / 2)],
            [.. huge[(huge.Length / 2)..], (byte)'\n', .. "after\n"u8.ToArray()],
        ]);

        Assert.AreSequenceEqual(["ok", "after"], records.Select(static r => Encoding.ASCII.GetString(r)).ToList());
    }

    private static async Task<List<byte[]>> PumpAsync(byte[][] chunks)
    {
        var pipe = new Pipe();
        var records = new List<byte[]>();
        Task<long> pump = VelaSshClientWrapper.PumpRecordsAsync(pipe.Reader, (byte)'\n', record =>
        {
            records.Add(record.ToArray());
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        foreach (byte[] chunk in chunks)
        {
            await pipe.Writer.WriteAsync(chunk);
        }
        await pipe.Writer.CompleteAsync();
        long count = await pump.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(records.Count, count);
        return records;
    }
}
