using System.Text;
using VelaShell.Core.Import;

namespace VelaShell.Core.Tests.Import;

/// <summary>CSV 单元格:引号规则与 CSV 注入防护(连接文件与审计日志导出共用)。</summary>
[TestClass]
[TestCategory("Import")]
public sealed class CsvTextTests
{
    [TestMethod]
    [DataRow("=HYPERLINK(\"http://evil\",\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"")]
    [DataRow("+cmd|' /C calc'!A0", "'+cmd|' /C calc'!A0")]
    [DataRow("-2+3", "'-2+3")]
    [DataRow("@SUM(A1)", "'@SUM(A1)")]
    [DataRow("\tleading tab", "\"'\tleading tab\"")]
    public void FormulaLeads_GetASingleQuote(string value, string expected) =>
        Assert.AreEqual(expected, CsvText.Cell(value));

    [TestMethod]
    [DataRow("plain", "plain")]
    [DataRow("a,b", "\"a,b\"")]
    [DataRow("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [DataRow("two\nlines", "\"two\nlines\"")]
    [DataRow(" padded ", "\" padded \"")]
    [DataRow("root@10.0.0.1:22 → root@10.0.0.2:22", "root@10.0.0.1:22 → root@10.0.0.2:22")]
    [DataRow("", "")]
    public void OrdinaryValues_FollowRfc4180(string value, string expected) =>
        Assert.AreEqual(expected, CsvText.Cell(value));

    [TestMethod]
    public void AppendRow_SeparatesWithCommas_AndEndsWithCrLf()
    {
        var builder = new StringBuilder();
        CsvText.AppendRow(builder, ["a", "b,c", "=1"]);
        Assert.AreEqual("a,\"b,c\",'=1\r\n", builder.ToString());
    }
}
