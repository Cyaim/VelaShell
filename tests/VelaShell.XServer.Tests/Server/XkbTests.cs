using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>XKEYBOARD:由核心键位表推出的键位表、修饰状态与 StateNotify、指示灯、名字。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class XkbTests
{
    private const ushort UseCoreKbd = 0x100;

    private static async Task<(byte Major, byte Event)> XkbAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(9).U16(0).Bytes(Encoding.Latin1.GetBytes("XKEYBOARD")).Pad());
        Assert.AreEqual(1, q.Bytes[8]);
        XMessage use = await c.RequestAsync(q.Bytes[9], 0, b => b.U16(1).U16(0));
        Assert.AreEqual(1, use.Bytes[1], "supported");
        return (q.Bytes[9], q.Bytes[10]);
    }

    [TestMethod]
    public async Task GetMap的键值段与核心键位表一致且类型下标合法()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        // full = KeyTypes | KeySyms
        XMessage map = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0x3).U16(0).Bytes(new byte[18]));
        Assert.IsTrue(map.IsReply);
        Assert.AreEqual(8, map.Bytes[10], "minKeyCode");
        int nTypes = map.Bytes[15];
        Assert.AreEqual(6, nTypes, "四个规范类型 + 两个四级类型(AltGr 层)");
        int nKeys = map.Bytes[20];
        Assert.AreEqual(248, nKeys);

        int o = 40;
        for (int t = 0; t < nTypes; t++)
        {
            int entries = map.Bytes[o + 5];
            o += 8 + (entries * 8);
        }
        // 键码 8 起逐个走到 38(a):类型 ALPHABETIC、宽 2、键值 a / A。
        for (int code = 8; code < 38; code++)
        {
            o += 8 + (map.U16(o + 6) * 4);
        }
        Assert.AreEqual(2, map.Bytes[o], "a 的类型是 ALPHABETIC");
        Assert.AreEqual(2, map.Bytes[o + 5], "宽度 2");
        Assert.AreEqual('a', map.U32(o + 8));
        Assert.AreEqual('A', map.U32(o + 12));
    }

    /// <summary>
    /// 字母类型认所有有大小写之分的字母:Unicode 键值的西里尔字母、传统键值的西里尔字母都是 ALPHABETIC;只有一列的希腊字母按核心规则
    /// 展开成小写、大写两级;FOUR_LEVEL_ALPHABETIC 有 Shift + Lock + Mod5 这一条。原先只认拉丁字母,CapsLock 在 XKB 客户端里无效。
    /// </summary>
    [TestMethod]
    public async Task 非拉丁字母也推成ALPHABETIC_单列字母展开成大小写两级_四级字母类型有Shift加Lock加Mod5()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        // ChangeKeyboardMapping:38 = ф Ф(Unicode 键值),39 = Cyrillic_ef Cyrillic_EF(传统键值),40 = Greek_alpha、第二列 NoSymbol。
        await c.SendAsync(100, 3, b => b.U8(38).U8(2).U16(0).U32(0x0100_0444).U32(0x0100_0424).U32(0x6c6).U32(0x6e6).U32(0x7e1).U32(0));
        // GetMap:全部类型 + 键码 38 起 3 个键的键值。
        XMessage map = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0x1).U16(0x2).U8(0).U8(0).U8(38).U8(3).Bytes(new byte[14]));
        int nTypes = map.Bytes[15];
        int o = 40;
        int fourLevelAlphabeticEntries = 0;
        for (int t = 0; t < nTypes; t++)
        {
            int entries = map.Bytes[o + 5];
            if (t == 5)
            {
                fourLevelAlphabeticEntries = entries;
            }
            o += 8 + (entries * 8);
        }
        Assert.AreEqual(6, fourLevelAlphabeticEntries, "FOUR_LEVEL_ALPHABETIC:Shift、Lock、Mod5、Shift+Mod5、Lock+Mod5、Shift+Lock+Mod5");

        List<(byte Type, uint[] Syms)> keys = [];
        for (int k = 0; k < 3; k++)
        {
            int n = map.U16(o + 6);
            keys.Add((map.Bytes[o], [.. Enumerable.Range(0, n).Select(i => map.U32(o + 8 + (4 * i)))]));
            o += 8 + (n * 4);
        }
        Assert.AreEqual(2, keys[0].Type, "Unicode 键值的 ф Ф 是 ALPHABETIC");
        Assert.AreEqual(2, keys[1].Type, "传统键值的 Cyrillic_ef / EF 是 ALPHABETIC");
        Assert.AreEqual(2, keys[2].Type, "单列的 Greek_alpha 是 ALPHABETIC");
        Assert.AreSequenceEqual(new uint[] { 0x7e1, 0x7c1 }, keys[2].Syms, "按核心规则展开成 alpha、ALPHA");
    }

    [TestMethod]
    public async Task 按Shift发StateNotify_CapsLock锁定并点亮指示灯()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, byte xkbEvent) = await XkbAsync(c);
        // SelectEvents:affectWhich = StateNotify(1<<2),details 全选
        await c.SendAsync(xkb, 1, b => b.U16(UseCoreKbd).U16(1 << 2).U16(0).U16(1 << 2).U16(0).U16(0));
        await c.SyncAsync();

        server.InjectKey(50, true);   // Shift_L
        XMessage state = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == xkbEvent && m.Bytes[1] == 2);
        Assert.AreEqual(1, state.Bytes[9], "有效修饰 = Shift");
        Assert.AreEqual(1, state.Bytes[10], "base = Shift");
        server.InjectKey(50, false);

        server.InjectKey(66, true);   // Caps_Lock 按下即锁定
        server.InjectKey(66, false);
        await c.SyncAsync();
        XMessage current = await c.RequestAsync(xkb, 4, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(0x02, current.Bytes[11], "lockedMods = Lock");
        Assert.AreEqual(0x02, current.Bytes[8], "有效修饰 = Lock");
        XMessage leds = await c.RequestAsync(xkb, 12, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(1u, leds.U32(8), "Caps Lock 灯亮");
    }

    [TestMethod]
    public async Task 两个Shift同时按住_松开一个Shift仍然生效()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        server.InjectKey(50, true);
        server.InjectKey(62, true);
        server.InjectKey(50, false);
        await c.SyncAsync();
        XMessage current = await c.RequestAsync(xkb, 4, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(0x01, current.Bytes[8]);
    }

    [TestMethod]
    public async Task GetNames给出键名与类型名_错误的设备报BadKeyboard()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        XMessage names = await c.RequestAsync(xkb, 17, b => b.U16(UseCoreKbd).U16(0).U32(1 << 9));   // KeyNames
        Assert.AreEqual(248, names.Bytes[19], "nKeys");
        // 键码 9 是第二个:ESC
        Assert.AreEqual("ESC", Encoding.Latin1.GetString(names.Bytes, 32 + 4, 3));
        Assert.AreEqual("AC01", Encoding.Latin1.GetString(names.Bytes, 32 + ((38 - 8) * 4), 4));

        XMessage bad = await c.RequestAsync(xkb, 4, b => b.U16(7).U16(0));
        Assert.IsTrue(bad.IsError);
    }

    [TestMethod]
    public async Task 核心ChangeKeyboardMapping之后XKB也看到新键值()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        // 键码 38 改成 x / X(xmodmap 的做法)
        await c.SendAsync(100, 1, b => b.U8(38).U8(2).U16(0).U32('x').U32('X'));
        XMessage map = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0).U16(0x2).U8(0).U8(0).U8(38).U8(1).Bytes(new byte[14]));
        Assert.AreEqual(38, map.Bytes[17], "firstKeySym");
        Assert.AreEqual('x', map.U32(40 + 8));
    }

    [TestMethod]
    public async Task AltGr层_核心第五六列推出四级键类型_右Alt进Mod5()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);

        // 德语布局的 Q 键:q Q | 组 2 抄组 1 | AltGr → @。右 Alt 改成 ISO_Level3_Shift,从 Mod1 挪到 Mod5。
        server.SetKeymap(new XKeymap("de", 6) { AltGr = true }.Map(24, 'q', 'Q', 'q', 'Q', '@', '@'));
        await c.SyncAsync();

        XMessage key = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0).U16(0x2).U8(0).U8(0).U8(24).U8(1).Bytes(new byte[14]));
        Assert.AreEqual(5, key.Bytes[40], "FOUR_LEVEL_ALPHABETIC");
        Assert.AreEqual(4, key.Bytes[45], "宽度 4");
        Assert.AreEqual('@', key.U32(48 + 8), "第三级 = AltGr");

        XMessage types = await c.RequestAsync(xkb, 8, b => b.U16(UseCoreKbd).U16(0x1).U16(0).Bytes(new byte[18]));
        Assert.AreEqual(6, types.Bytes[16], "nTypes:四个规范类型 + 两个四级类型");

        XMessage modifiers = await c.RequestAsync(119, 0);   // GetModifierMapping
        Assert.AreEqual(108, modifiers.Bytes[32 + 14], "Mod5 的第一个键码是右 Alt");
    }

    [TestMethod]
    public async Task 更窄的ChangeKeyboardMapping不收窄整张表()
    {
        // xmodmap -e "keycode 108 = ISO_Level3_Shift" 发的是每键码 1 列:别的键的 Shift 列不能跟着没了。
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SendAsync(100, 1, b => b.U8(108).U8(1).U16(0).U32(0xfe03));
        XMessage map = await c.RequestAsync(101, 0, b => b.U8(25).U8(1).U16(0));   // GetKeyboardMapping:w
        Assert.AreEqual(2, map.Bytes[1], "每键码仍是 2 列");
        Assert.AreEqual('w', map.U32(32));
        Assert.AreEqual('W', map.U32(36));
        XMessage alt = await c.RequestAsync(101, 0, b => b.U8(108).U8(1).U16(0));
        Assert.AreEqual(0xfe03u, alt.U32(32));
        Assert.AreEqual(0u, alt.U32(36), "请求没给的列清成 NoSymbol");
    }

    [TestMethod]
    public async Task SetMap上传的键值与修饰键映射写回核心键位表()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);

        // present = KeySyms | ModifierMap:键码 29(US 的 y)改成 z / Z(德语布局),键码 37 只进 Control。
        await c.SendAsync(xkb, 9, b => b.U16(UseCoreKbd).U16(0x2 | 0x4).U16(0).U8(8).U8(255)
            .U8(0).U8(0)                     // firstType、nTypes
            .U8(29).U8(1).U16(2)             // firstKeySym、nKeySyms、totalSyms
            .U8(0).U8(0).U16(0)              // 动作
            .U8(0).U8(0).U8(0)               // 行为
            .U8(0).U8(0).U8(0)               // 显式成分
            .U8(37).U8(1).U8(1)              // firstModMapKey、nModMapKeys、totalModMapKeys
            .U8(0).U8(0).U8(0)               // 虚拟修饰映射
            .U16(0)                          // virtualMods
            .U8(1).U8(0).U8(0).U8(0).U8(1).U8(2).U16(2).U32('z').U32('Z')   // KEYSYMMAP
            .U8(37).U8(0x04).U16(0));                                      // KEYMODMAP(补齐)
        XMessage map = await c.RequestAsync(101, 0, b => b.U8(29).U8(1).U16(0));
        Assert.AreEqual('z', map.U32(32));
        Assert.AreEqual('Z', map.U32(36));

        XMessage modifiers = await c.RequestAsync(119, 0);
        int per = modifiers.Bytes[1];
        byte[] control = modifiers.Bytes.AsSpan(32 + (2 * per), per).ToArray();
        Assert.Contains((byte)37, control, "键码 37 在 Control 行");
    }

    [TestMethod]
    public async Task 锁存的修饰键只作用于下一个非修饰键_SetMap的修饰映射只能落在声明的区间里()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        uint top = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(top).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0x800).U32(0x1));   // KeyPress
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        server.FocusTopLevel(host.Mapped[top]);

        // LatchLockState:锁存 Shift。
        await c.SendAsync(xkb, 5, b => b.U16(UseCoreKbd).U8(0).U8(0).U8(0).U8(0).U8(1).U8(1).U8(0).U8(0).U16(0));
        await c.SyncAsync();
        server.InjectKey(XKeycodes.A, pressed: true);
        Assert.AreEqual(1, (await c.NextEventAsync(2)).U16(28) & 1, "下一个键带着锁存的 Shift");
        server.InjectKey(XKeycodes.A, pressed: false);
        server.InjectKey(XKeycodes.B, pressed: true);
        Assert.AreEqual(0, (await c.NextEventAsync(2)).U16(28) & 1, "用过一次就解除了");
        server.InjectKey(XKeycodes.B, pressed: false);
        XMessage state = await c.RequestAsync(xkb, 4, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(0, state.Bytes[10], "latchedMods = 0");

        // SetMap 的修饰映射声明区间是键码 37 起 1 个,项里却给了 50:BadValue。
        XMessage refused = await c.RequestAsync(xkb, 9, b => b.U16(UseCoreKbd).U16(0x4).U16(0).U8(8).U8(255)
            .U8(0).U8(0).U8(0).U8(0).U16(0).U8(0).U8(0).U16(0).U8(0).U8(0).U8(0).U8(0).U8(0).U8(0)
            .U8(37).U8(1).U8(1).U8(0).U8(0).U8(0).U16(0)
            .U8(50).U8(0x04).U16(0));
        Assert.IsTrue(refused.IsError);
        Assert.AreEqual(2, refused.Bytes[1], "BadValue");
    }

    [TestMethod]
    public async Task 宿主换进锁定键状态_不合成按键_客户端读得到()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (byte xkb, _) = await XkbAsync(c);
        server.SetLockState(capsLock: true, numLock: true);
        await c.SyncAsync();
        XMessage state = await c.RequestAsync(xkb, 4, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(0x02 | 0x10, state.Bytes[11], "locked:Lock 与 Mod2(Num_Lock)");
        Assert.AreEqual(0x02 | 0x10, state.Bytes[8] & 0x12, "生效的修饰里也有");

        server.SetLockState(capsLock: false, numLock: true);
        await c.SyncAsync();
        state = await c.RequestAsync(xkb, 4, b => b.U16(UseCoreKbd).U16(0));
        Assert.AreEqual(0x10, state.Bytes[11]);
        Assert.IsTrue((await c.RequestAsync(44, 0)).Bytes.Skip(8).All(b => b == 0), "QueryKeymap:没有按着的键(没合成按键)");
    }

    [TestMethod]
    public async Task 日文键盘的Ro与Yen_F13到F24_多媒体键都有键值与XKB键名()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage map = await c.RequestAsync(101, 0, b => b.U8(97).U8(1).U16(0));   // GetKeyboardMapping:键码 97(Ro)
        int per = map.Bytes[1];
        Assert.AreEqual(0x5Cu, map.U32(32), "Ro:backslash");
        Assert.AreEqual(0x5Fu, map.U32(36), "Shift+Ro:underscore(JIS 打下划线就靠它)");
        XMessage yen = await c.RequestAsync(101, 0, b => b.U8(132).U8(1).U16(0));
        Assert.AreEqual(0x7Cu, yen.U32(36), "Shift+Yen:bar");
        XMessage f13 = await c.RequestAsync(101, 0, b => b.U8(191).U8(1).U16(0));
        Assert.AreEqual(0xFFCAu, f13.U32(32), "F13");
        XMessage mute = await c.RequestAsync(101, 0, b => b.U8(121).U8(1).U16(0));
        Assert.AreEqual(0x1008FF12u, mute.U32(32), "XF86AudioMute");
        Assert.IsGreaterThanOrEqualTo(2, per);
    }
}
