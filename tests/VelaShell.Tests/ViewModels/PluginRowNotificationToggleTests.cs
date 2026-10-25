using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>插件管理页一行上的「静音通知 / 恢复通知」:文案跟着静音状态走,悬停说明写明已发的消息怎么处理。</summary>
[TestClass]
[TestCategory("Plugins")]
public class PluginRowNotificationToggleTests
{
    [TestMethod]
    [DataRow(false, "PluginManager_MuteNotifications")]
    [DataRow(true, "PluginManager_UnmuteNotifications")]
    public void ToggleTextFollowsTheMuteState(bool muted, string key)
    {
        var row = new PluginRowViewModel(new PluginDescriptor { Directory = "plugin" }, false)
        {
            NotificationsMuted = muted,
            CanToggleNotifications = true
        };

        Assert.AreEqual(Strings.Get(key), row.NotificationsToggleText);
        Assert.AreEqual(Strings.Get("PluginManager_MuteNotificationsTip"), row.NotificationsToggleTip);
    }
}
