using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// keyboard-interactive 的应答:认得出的口令提示用已有的密码代答一次,其余交给界面;
/// 非密码认证不回退到口令;纯展示的一轮不弹框;对端文字先清洗再上界面。
/// </summary>
/// <remarks>
/// 真实 OpenSSH + PAM 上的端到端用例在 <c>VelaShell.Core.Tests</c> 的 <c>KeyboardInteractiveIntegrationTests</c>。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class KeyboardInteractiveResponderTests
{
    private const string Target = "root@bastion:22";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PamPasswordThenCode_ThePasswordIsAnsweredAndOnlyTheCodeIsAsked()
    {
        var prompt = new ScriptedPrompt(_ => ["123456"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        Assert.AreSequenceEqual(["hunter2"], [.. (await Respond(responder, Round(("Password: ", false))))]);
        Assert.AreSequenceEqual(["123456"], [.. (await Respond(responder, Round(("Verification code: ", false))))]);

        KeyboardInteractiveRequest asked = prompt.Requests.Single();
        Assert.AreEqual(Target, asked.Target);
        Assert.AreEqual(new KeyboardInteractiveField("Verification code:", false), asked.Fields.Single());
    }

    [TestMethod]
    public async Task ASecondPasswordRound_IsAskedRatherThanRepeatingTheSamePassword()
    {
        // 同一次认证里再问口令(改密码流程的 New password:、或者刚才那个不对):交给用户
        var prompt = new ScriptedPrompt(_ => ["new-secret"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        await Respond(responder, Round(("Password: ", false)));
        Assert.AreSequenceEqual(["new-secret"], [.. (await Respond(responder, Round(("New password: ", false))))]);
        Assert.HasCount(1, prompt.Requests);
    }

    [TestMethod]
    public async Task AnEmptySavedPassword_IsAskedFor()
    {
        var prompt = new ScriptedPrompt(_ => ["typed"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "");

        Assert.AreSequenceEqual(["typed"], [.. (await Respond(responder, Round(("Password: ", false))))]);
    }

    [TestMethod]
    [DataRow("Password + OTP: ")]
    [DataRow("Password and verification code: ")]
    [DataRow("密码+动态码:")]
    public async Task APasswordPromptThatAlsoWantsACode_IsAsked(string text)
    {
        var prompt = new ScriptedPrompt(_ => ["hunter2123456"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        await Respond(responder, Round((text, false)));

        Assert.HasCount(1, prompt.Requests, "要的是口令与动态码拼起来的串,不能只填口令");
    }

    [TestMethod]
    public async Task KeyBasedAuthentication_DeclinesALonePasswordPrompt_ButAsksForTheCode()
    {
        // 私钥 / 证书 / agent:钥被拒之后冒出来的「Password:」不回退成口令认证,答空串让服务端拒掉
        var prompt = new ScriptedPrompt(_ => ["654321"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, password: null);

        Assert.AreSequenceEqual([""], [.. (await Respond(responder, Round(("Password: ", false))))]);
        Assert.IsEmpty(prompt.Requests);

        Assert.AreSequenceEqual(["654321"], [.. (await Respond(responder, Round(("Verification code: ", false))))]);
    }

    [TestMethod]
    public async Task EchoedAndMultiPromptRounds_AreAskedAsIs()
    {
        var prompt = new ScriptedPrompt(r => [.. r.Fields.Select((_, i) => $"a{i}")]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        IReadOnlyList<string> answers = await Respond(responder,
            Round(("Passcode or option (1-3): ", true), ("PIN: ", false)), name: "Duo", instruction: "Choose a method");

        Assert.AreSequenceEqual(["a0", "a1"], [.. answers]);
        KeyboardInteractiveRequest asked = prompt.Requests.Single();
        Assert.AreEqual("Duo", asked.Name);
        Assert.AreEqual("Choose a method", asked.Instruction);
        Assert.IsTrue(asked.Fields[0].Echo);
        Assert.IsFalse(asked.Fields[1].Echo);
    }

    [TestMethod]
    public async Task InformationalRounds_AreNotAsked_AndTheirTextShowsUpInTheNextPrompt()
    {
        var prompt = new ScriptedPrompt(_ => ["123456"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        IReadOnlyList<string> none = await Respond(responder, Round(), instruction: "Approve the push on your phone.");
        Assert.IsEmpty(none);
        Assert.IsEmpty(prompt.Requests, "纯展示的一轮不该弹框要人输入(规格 04 §6.4)");

        await Respond(responder, Round(("Verification code: ", false)), instruction: "Or type a code.");
        Assert.AreEqual("Approve the push on your phone.\nOr type a code.", prompt.Requests.Single().Instruction);
    }

    /// <summary>
    /// 用户点取消:抛一个令牌没触发的取消 —— 库认得出这一种,以 Aborted 结束连接(规格 08 §2.1),
    /// 装配处再报成「已取消」。
    /// </summary>
    [TestMethod]
    public async Task UserCancel_IsThrownAsACancellationWithoutATriggeredToken()
    {
        var responder = new KeyboardInteractiveResponder(new ScriptedPrompt(_ => null), Target, "hunter2");

        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => Respond(responder, Round(("Verification code: ", false))));
        Assert.IsFalse(error.CancellationToken.IsCancellationRequested);
    }

    /// <summary>服务端要求先改密码:新密码输两遍、对上了才交给库;框里带着服务端的原话(清洗过)。</summary>
    [TestMethod]
    public async Task PasswordChange_TheNewPasswordIsAskedTwiceAndHandedOverWhenBothMatch()
    {
        var prompt = new ScriptedPrompt(_ => ["n3w-secret", "n3w-secret"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        string? newPassword = await responder.AskNewPasswordAsync(
            new SshPasswordChangeRequest { Prompt = "Your password has expired.\u001b[2J", Attempt = 1 }, TestContext.CancellationToken);

        Assert.AreEqual("n3w-secret", newPassword);
        KeyboardInteractiveRequest asked = prompt.Requests.Single();
        Assert.AreEqual(Target, asked.Target);
        Assert.AreEqual(Strings.Get("SshPwdChange_Title"), asked.Name);
        Assert.AreSequenceEqual(
            new[] { new KeyboardInteractiveField(Strings.Get("SshPwdChange_NewPassword"), false), new KeyboardInteractiveField(Strings.Get("SshPwdChange_ConfirmPassword"), false) },
            [.. asked.Fields]);
        Assert.StartsWith(Strings.Get("SshPwdChange_Required"), asked.Instruction);
        Assert.Contains("Your password has expired.", asked.Instruction);
        Assert.DoesNotContain("\u001b", asked.Instruction, "对端文字先清洗再上界面");
    }

    /// <summary>两遍对不上、或者空着:不交给库(协议里没有确认这一步,输错的会直接成为账户密码),说明原因再问。</summary>
    [TestMethod]
    public async Task PasswordChange_MismatchedOrEmptyEntriesAreAskedAgain()
    {
        Queue<IReadOnlyList<string>> script = new([["abc", "abd"], ["", ""], ["n3w-secret", "n3w-secret"]]);
        var prompt = new ScriptedPrompt(_ => script.Dequeue());
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        string? newPassword = await responder.AskNewPasswordAsync(
            new SshPasswordChangeRequest { Prompt = "", Attempt = 1 }, TestContext.CancellationToken);

        Assert.AreEqual("n3w-secret", newPassword);
        Assert.HasCount(3, prompt.Requests);
        Assert.AreEqual(Strings.Get("SshPwdChange_Required"), prompt.Requests[0].Instruction);
        Assert.AreEqual(Strings.Get("SshPwdChange_Mismatch"), prompt.Requests[1].Instruction);
        Assert.AreEqual(Strings.Get("SshPwdChange_Mismatch"), prompt.Requests[2].Instruction);
    }

    /// <summary>服务端不接受上一次的新密码(第二次问):说明换成「请换一个」。</summary>
    [TestMethod]
    public async Task PasswordChange_ASecondAttemptSaysTheLastOneWasRejected()
    {
        var prompt = new ScriptedPrompt(_ => ["n3w-secret", "n3w-secret"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        await responder.AskNewPasswordAsync(
            new SshPasswordChangeRequest { Prompt = "Password too short.", Attempt = 2 }, TestContext.CancellationToken);

        Assert.AreEqual($"{Strings.Get("SshPwdChange_Rejected")}\nPassword too short.", prompt.Requests.Single().Instruction);
    }

    /// <summary>在改密码框上点取消:与动态码框同一个口径(令牌没触发的取消),并记下是改密码框 —— 装配处据此报「已取消修改密码」。</summary>
    [TestMethod]
    public async Task PasswordChange_UserCancelIsACancellationAndIsRemembered()
    {
        var responder = new KeyboardInteractiveResponder(new ScriptedPrompt(_ => null), Target, "hunter2");

        OperationCanceledException error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => responder.AskNewPasswordAsync(new SshPasswordChangeRequest { Prompt = "", Attempt = 1 }, TestContext.CancellationToken).AsTask());

        Assert.IsFalse(error.CancellationToken.IsCancellationRequested);
        Assert.IsTrue(responder.PasswordChangeCancelled);
    }

    [TestMethod]
    public async Task ATriggeredToken_IsACancellation_NotAUserCancel()
    {
        using var cts = new CancellationTokenSource();
        var prompt = new ScriptedPrompt(_ =>
        {
            cts.Cancel();
            return null;
        });
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => responder.RespondAsync(Challenge(Round(("Verification code: ", false))), cts.Token).AsTask());
        Assert.AreEqual(cts.Token, error.CancellationToken, "关了标签 / 认证超时带着令牌抛:库按它自己的口径处理,不是用户在框上点的取消");
    }

    [TestMethod]
    public async Task AWrongNumberOfAnswers_IsRejectedHere_NotSentToTheServer()
    {
        var responder = new KeyboardInteractiveResponder(new ScriptedPrompt(_ => ["only-one"]), Target, "hunter2");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => Respond(responder, Round(("A: ", true), ("B: ", true))));
    }

    [TestMethod]
    public async Task PeerText_IsCleanedBeforeItReachesTheScreen()
    {
        var prompt = new ScriptedPrompt(_ => ["x"]);
        var responder = new KeyboardInteractiveResponder(prompt, Target, "hunter2");

        await Respond(responder, Round(("\u001b[31mCode\u202E:\u0007 ", false)),
            name: "Bank\u0000", instruction: "line1\r\nline2\t" + new string('x', 5000));

        KeyboardInteractiveRequest asked = prompt.Requests.Single();
        // 规则是库的 PeerText.Sanitize：换成 ? 而不是删掉 —— 被塞了控制字符这件事看得见。
        Assert.AreEqual("?[31mCode?:?", asked.Fields[0].Prompt, "ESC、BEL 与 RLO 这类控制符不能原样进界面");
        Assert.AreEqual("Bank?", asked.Name);
        Assert.StartsWith("line1\nline2 ", asked.Instruction);
        Assert.AreEqual(2048, asked.Instruction.Length);
        StringAssert.EndsWith(asked.Instruction, "…");
    }

    [TestMethod]
    [DataRow("Password:", true)]
    [DataRow("root@host's password: ", true)]
    [DataRow("(current) UNIX password: ", true)]
    [DataRow("请输入密码:", true)]
    [DataRow("パスワード:", true)]
    [DataRow("비밀번호:", true)]
    [DataRow("Verification code: ", false)]
    [DataRow("Passcode or option (1-3): ", false)]
    [DataRow("One-time password (OATH) for `root': ", false)]
    [DataRow("Enter PIN: ", false)]
    [DataRow("请输入验证码:", false)]
    public void LooksLikePasswordPrompt(string text, bool expected) =>
        Assert.AreEqual(expected, KeyboardInteractiveResponder.LooksLikePasswordPrompt(text), text);

    [TestMethod]
    public async Task TheCredentialWrapsTheResponder()
    {
        var prompt = new ScriptedPrompt(_ => ["123456"]);
        KeyboardInteractiveCredential credential = new KeyboardInteractiveResponder(prompt, Target, "hunter2").ToCredential();

        IReadOnlyList<string> answers = await credential.RespondAsync(
            Challenge(Round(("Verification code: ", false))), TestContext.CancellationToken);

        Assert.AreSequenceEqual(["123456"], [.. answers]);
        Assert.AreEqual("keyboard-interactive", credential.MethodName);
    }

    private Task<IReadOnlyList<string>> Respond(KeyboardInteractiveResponder responder,
        SshKeyboardPrompt[] prompts,
        string name = "",
        string instruction = "") =>
        responder.RespondAsync(Challenge(prompts, name, instruction), TestContext.CancellationToken).AsTask();

    private static SshKeyboardPrompt[] Round(params (string Text, bool Echo)[] prompts) =>
        [.. prompts.Select(p => new SshKeyboardPrompt(p.Text, p.Echo))];

    private static SshKeyboardChallenge Challenge(SshKeyboardPrompt[] prompts, string name = "", string instruction = "") =>
        new() { Name = name, Instruction = instruction, Prompts = prompts };

    private sealed class ScriptedPrompt(Func<KeyboardInteractiveRequest, IReadOnlyList<string>?> answer) : IKeyboardInteractivePrompt
    {
        public List<KeyboardInteractiveRequest> Requests { get; } = [];

        public Task<IReadOnlyList<string>?> AskAsync(KeyboardInteractiveRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }
}
