// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/06-sftp.md §4.7
//
// SFTP v3 没规定文件名编码：GBK、Shift-JIS、Latin-1 的老服务器上名字不是合法的 UTF-8。
// 曾经按 UTF-8 宽容解码，解不开的字节变成 U+FFFD，再编回去已经是另一串字节 —— 文件打不开、删不掉。

using System.Text;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Ssh.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SftpNameCodecTests
{
    [TestMethod]
    public void 任意字节都能无损往返()
    {
        Random random = new(20261005);
        for (int round = 0; round < 2000; round++)
        {
            byte[] bytes = new byte[random.Next(0, 40)];
            random.NextBytes(bytes);

            string name = SftpNameCodec.Utf8.Decode(bytes);

            Assert.AreSequenceEqual(bytes, SftpNameCodec.Utf8.Encode(name), $"第 {round} 轮：{Convert.ToHexString(bytes)}");
        }
    }

    [TestMethod]
    [DataRow("readme.txt")]
    [DataRow("中文名字.txt")]
    [DataRow("emoji 😀 名.md")]
    public void 合法的UTF8照常解_编回去一字不差(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);

        Assert.AreEqual(name, SftpNameCodec.Utf8.Decode(bytes));
        Assert.AreSequenceEqual(bytes, SftpNameCodec.Utf8.Encode(name));
    }

    [TestMethod]
    public void 解不开的字节单独转义_合法的部分照常解()
    {
        // GBK 的「中」是 D6 D0，按 UTF-8 解不开；后面的 .txt 照常。
        byte[] bytes = [0xD6, 0xD0, (byte)'.', (byte)'t', (byte)'x', (byte)'t'];

        string name = SftpNameCodec.Utf8.Decode(bytes);

        Assert.AreEqual("\uDCD6\uDCD0.txt", name);
    }

    [TestMethod]
    public void 选了别的编码就按那个编码解与编()
    {
        byte[] latin1 = [(byte)'c', (byte)'a', (byte)'f', 0xE9];
        var codec = SftpNameCodec.For(Encoding.Latin1);

        Assert.AreEqual("café", codec.Decode(latin1));
        Assert.AreSequenceEqual(latin1, codec.Encode("café"));
        Assert.AreSame(SftpNameCodec.Utf8, SftpNameCodec.For(Encoding.UTF8), "选 UTF-8 就是默认那一个（带无损转义）");
    }
}
