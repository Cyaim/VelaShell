using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 快捷命令的变量占位:<c>{{名字}}</c> / <c>{{名字=默认值}}</c>。
/// </summary>
/// <remarks>
/// 最要紧的是<b>别误伤</b>:运维命令里本来就有大量双花括号(Go 模板、Jinja、GitHub Actions),
/// 把它们当成占位的话,用户每点一次都会被问一个莫名其妙的「.State.Status」。
/// </remarks>
[TestClass]
[TestCategory("QuickCommands")]
public class QuickCommandTemplateTests
{
    [TestMethod]
    public void Parse_FindsNamesAndDefaults_InOrderOfFirstAppearance()
    {
        var template = QuickCommandTemplate.Parse("journalctl -u {{svc}} -n {{lines=200}} --since {{since=today}}");

        Assert.IsTrue(template.HasVariables);
        Assert.AreSequenceEqual(
            [new QuickCommandVariable("svc", ""), new QuickCommandVariable("lines", "200"), new QuickCommandVariable("since", "today")], [.. template.Variables]);
    }

    [TestMethod]
    public void Parse_SameNameTwice_AsksOnce_AndKeepsTheFirstNonEmptyDefault()
    {
        var template = QuickCommandTemplate.Parse("cp {{file}} {{file}}.bak && ls {{dir}} {{dir=/tmp}}");

        Assert.AreSequenceEqual(
            [new QuickCommandVariable("file", ""), new QuickCommandVariable("dir", "/tmp")], [.. template.Variables]);
    }

    [TestMethod]
    [DataRow("docker inspect -f '{{.State.Status}}' web")]
    [DataRow("docker ps --format '{{json .}}'")]
    [DataRow("kubectl get pods -o go-template='{{range .items}}{{.metadata.name}}{{\"\\n\"}}{{end}}'")]
    [DataRow("ansible all -m debug -a 'msg={{ inventory_hostname }}'")]
    [DataRow("echo '{{if .Ok}}yes{{else}}no{{end}}'")]
    [DataRow("echo '${{ secrets.TOKEN }}'")]
    [DataRow("echo {{}} {{=x}} {{1st}}")]
    public void Parse_LeavesTemplateLanguagesAlone(string command)
    {
        var template = QuickCommandTemplate.Parse(command);

        Assert.IsFalse(template.HasVariables, string.Join(", ", template.Variables));
        Assert.AreEqual(command, template.Render(new Dictionary<string, string>()));
    }

    [TestMethod]
    public void Parse_AcceptsNonAsciiAndHyphenatedNames()
    {
        var template = QuickCommandTemplate.Parse("kubectl logs {{pod-name}} -n {{命名空间=default}}");

        Assert.AreSequenceEqual(["pod-name", "命名空间"], [.. template.Variables.Select(v => v.Name)]);
    }

    [TestMethod]
    public void Render_UsesGivenValues_ThenDefaults_AndLeavesGoActionsInPlace()
    {
        var template = QuickCommandTemplate.Parse(
            "docker ps -f name={{name}} --format '{{.Names}}' | head -n {{n=5}}");

        string rendered = template.Render(new Dictionary<string, string> { ["name"] = "web" });

        Assert.AreEqual("docker ps -f name=web --format '{{.Names}}' | head -n 5", rendered);
    }

    [TestMethod]
    public void Render_FlattensLineBreaksInValues()
    {
        // 快捷命令只发正文、不带回车;值里夹一个换行就等于替用户按了回车,命令提前执行
        var template = QuickCommandTemplate.Parse("grep {{pattern}} app.log");

        string rendered = template.Render(new Dictionary<string, string> { ["pattern"] = "a\r\nb\nc\rd" });

        Assert.AreEqual("grep a b c d app.log", rendered);
    }

    [TestMethod]
    public void Render_WithoutVariables_ReturnsTheTextVerbatim()
    {
        var template = QuickCommandTemplate.Parse("uptime");

        Assert.IsFalse(template.HasVariables);
        Assert.AreEqual("uptime", template.Render(new Dictionary<string, string> { ["unused"] = "x" }));
    }
}
