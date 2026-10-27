using VelaShell.Core.Layouts;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 布局模板的同步合并:按 Id 逐条比时间、删除靠墓碑、重名照存;结果与先后、次数无关,两边一致时不再要求推送。
/// </summary>
[TestClass]
public sealed class LayoutTemplateMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static LayoutTemplate Template(string name, DateTimeOffset updatedAt, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt,
        Root = new() { Documents = [new() { Kind = LayoutDocumentKind.Terminal, ProfileId = Guid.NewGuid(), Title = name }] },
    };

    private static LayoutTemplateTombstone Tombstone(Guid id, DateTimeOffset deletedAt) => new() { Id = id, DeletedAt = deletedAt };

    private static LayoutTemplateSyncState State(IEnumerable<LayoutTemplate>? templates = null, IEnumerable<LayoutTemplateTombstone>? tombstones = null) =>
        new([.. templates ?? []], [.. tombstones ?? []]);

    /// <summary>把合并结果「落」到一端:本机就是照着 LocalUpserts / LocalTombstones / LocalExpired 改,云端就是换成 Merged。</summary>
    private static LayoutTemplateSyncState ApplyLocally(LayoutTemplateSyncState local, LayoutTemplateMergeResult result)
    {
        var templates = local.Templates.ToDictionary(t => t.Id);
        var tombstones = local.Tombstones.ToDictionary(t => t.Id);
        foreach (LayoutTemplate upsert in result.LocalUpserts)
        {
            templates[upsert.Id] = upsert;
            tombstones.Remove(upsert.Id);
        }
        foreach (LayoutTemplateTombstone tombstone in result.LocalTombstones)
        {
            tombstones[tombstone.Id] = tombstone;
            templates.Remove(tombstone.Id);
        }
        foreach (Guid id in result.LocalExpired)
        {
            templates.Remove(id);
            tombstones.Remove(id);
        }
        return State(templates.Values, tombstones.Values);
    }

    private static string Describe(LayoutTemplateSyncState state) =>
        string.Join(";", state.Templates.OrderBy(t => t.Id).Select(t => $"{t.Id:N}@{t.UpdatedAt:O}")
                                       .Concat(state.Tombstones.OrderBy(t => t.Id).Select(t => $"x{t.Id:N}@{t.DeletedAt:O}")));

    [TestMethod]
    public void TemplatesSavedOnEitherSide_EndUpOnBoth()
    {
        LayoutTemplate mine = Template("机房 A", Now.AddHours(-2));
        LayoutTemplate theirs = Template("机房 B", Now.AddHours(-1));

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(State([mine]), State([theirs]), Now);

        Assert.AreSequenceEqual(["机房 A", "机房 B"], [.. result.Merged.Templates.Select(t => t.Name)]);
        Assert.AreSequenceEqual([theirs.Id], [.. result.LocalUpserts.Select(t => t.Id)], "本机只需要收下对方那一个");
        Assert.IsTrue(result.RemoteChanged, "云端也缺本机那一个");
    }

    [TestMethod]
    public void EachTemplate_TakesTheLaterEdit_Independently()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        LayoutTemplateSyncState local = State([Template("a-local", Now.AddHours(-1), a), Template("b-local", Now.AddHours(-5), b)]);
        LayoutTemplateSyncState remote = State([Template("a-remote", Now.AddHours(-3), a), Template("b-remote", Now.AddHours(-2), b)]);

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(local, remote, Now);

        Assert.AreSequenceEqual(["a-local", "b-remote"], [.. result.Merged.Templates.Select(t => t.Name)],
            "不是整份较新者胜:a 本机改得晚、b 云端改得晚,各留各的");
        Assert.AreEqual("b-remote", result.LocalUpserts.Single().Name);
        Assert.IsTrue(result.RemoteChanged, "a 的新版本要推上去");
    }

    [TestMethod]
    public void ALaterDeletion_RemovesTheTemplate_AndKeepsTheTombstone()
    {
        LayoutTemplate template = Template("prod", Now.AddDays(-1));
        LayoutTemplateSyncState remote = State(tombstones: [Tombstone(template.Id, Now.AddHours(-1))]);

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(State([template]), remote, Now);

        Assert.IsEmpty(result.Merged.Templates);
        Assert.AreEqual(template.Id, result.LocalTombstones.Single().Id, "本机删掉它,并记下墓碑(以后再往别处传)");
        Assert.AreEqual(template.Id, result.Merged.Tombstones.Single().Id);
        Assert.IsFalse(result.RemoteChanged, "云端早就是这个样子");
    }

    [TestMethod]
    public void AnEditAfterTheDeletion_BringsTheTemplateBack()
    {
        LayoutTemplate edited = Template("prod", Now.AddMinutes(-5));
        LayoutTemplateSyncState remote = State(tombstones: [Tombstone(edited.Id, Now.AddHours(-1))]);

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(State([edited]), remote, Now);

        Assert.AreEqual(edited.Id, result.Merged.Templates.Single().Id);
        Assert.IsEmpty(result.Merged.Tombstones);
        Assert.IsFalse(result.LocalChanged);
        Assert.IsTrue(result.RemoteChanged);
    }

    [TestMethod]
    public void AtTheSameInstant_TheTombstoneWins()
    {
        LayoutTemplate template = Template("prod", Now.AddHours(-1));
        LayoutTemplateSyncState remote = State(tombstones: [Tombstone(template.Id, template.UpdatedAt)]);

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(State([template]), remote, Now);

        Assert.IsEmpty(result.Merged.Templates, "删除总是看过那个版本之后才做的");
        Assert.HasCount(1, result.LocalTombstones);
    }

    [TestMethod]
    public void SameNameDifferentIds_AreBothKept_WithoutRenaming()
    {
        LayoutTemplate mine = Template("prod", Now.AddHours(-2));
        LayoutTemplate theirs = Template("PROD", Now.AddHours(-1));

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(State([mine]), State([theirs]), Now);

        Assert.HasCount(2, result.Merged.Templates);
        Assert.AreSequenceEqual(["prod", "PROD"], [.. result.Merged.Templates.Select(t => t.Name)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder, "两套不同的分屏碰巧同名:不合并、不改名,用户自己分辨");
    }

    [TestMethod]
    public void EqualTimestamps_AreTreatedAsTheSameCopy_SoNothingIsPushedAgain()
    {
        var id = Guid.NewGuid();
        LayoutTemplateSyncState local = State([Template("prod", Now.AddHours(-1), id)]);
        LayoutTemplateSyncState remote = State([Template("prod", Now.AddHours(-1), id)]);

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(local, remote, Now);

        Assert.IsFalse(result.LocalChanged);
        Assert.IsFalse(result.RemoteChanged, "按内容比会让每次同步都推一遍");
    }

    [TestMethod]
    public void MergingIsOrderIndependent_AndASecondMergeChangesNothing()
    {
        var shared = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        LayoutTemplateSyncState a = State(
            [Template("a only", Now.AddHours(-3)), Template("shared (a)", Now.AddHours(-1), shared), Template("deleted later", Now.AddHours(-4), deleted)]);
        LayoutTemplateSyncState b = State(
            [Template("b only", Now.AddHours(-2)), Template("shared (b)", Now.AddHours(-2), shared)],
            [Tombstone(deleted, Now.AddHours(-3))]);

        LayoutTemplateMergeResult ab = LayoutTemplateMerge.Merge(a, b, Now);
        LayoutTemplateMergeResult ba = LayoutTemplateMerge.Merge(b, a, Now);

        Assert.AreEqual(Describe(ab.Merged), Describe(ba.Merged), "谁合并谁结果都一样");
        LayoutTemplateSyncState aAfter = ApplyLocally(a, ab);
        Assert.AreEqual(Describe(ab.Merged), Describe(aAfter), "本机照着改完就是合并结果");
        LayoutTemplateMergeResult again = LayoutTemplateMerge.Merge(aAfter, ab.Merged, Now);
        Assert.IsFalse(again.LocalChanged);
        Assert.IsFalse(again.RemoteChanged, "推上去之后再合并一次:两边都不用动");
    }

    [TestMethod]
    public void TombstonesPastTheRetention_AreDroppedEverywhere()
    {
        LayoutTemplate stale = Template("old", Now.AddDays(-400));
        DateTimeOffset deletedAt = Now - LayoutTemplateMerge.TombstoneRetention - TimeSpan.FromDays(1);
        LayoutTemplateSyncState remote = State(tombstones: [Tombstone(stale.Id, deletedAt)]);

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(State([stale]), remote, Now);

        Assert.IsEmpty(result.Merged.Templates, "过期之前的删除照样生效");
        Assert.IsEmpty(result.Merged.Tombstones, "墓碑本身不再留");
        Assert.AreEqual(stale.Id, result.LocalExpired.Single());
        Assert.IsTrue(result.RemoteChanged, "云端那块过期墓碑也要清掉");
    }

    [TestMethod]
    public void TombstonesWithinTheRetention_KeepPropagating()
    {
        var id = Guid.NewGuid();
        DateTimeOffset deletedAt = Now - LayoutTemplateMerge.TombstoneRetention + TimeSpan.FromDays(1);

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(State(tombstones: [Tombstone(id, deletedAt)]), remote: null, Now);

        Assert.AreEqual(id, result.Merged.Tombstones.Single().Id);
        Assert.IsTrue(result.RemoteChanged, "云端还没有这份文件:墓碑要跟着推上去");
    }

    [TestMethod]
    public void NothingOnEitherSide_NeedsNothing()
    {
        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(LayoutTemplateSyncState.Empty, remote: null, Now);

        Assert.IsFalse(result.LocalChanged);
        Assert.IsFalse(result.RemoteChanged, "没用过模板的人不该在 Gist 里多出一个空文件");
    }

    [TestMethod]
    public void BrokenEntriesFromTheCloud_AreIgnored()
    {
        LayoutTemplate good = Template("good", Now.AddHours(-1));
        LayoutTemplate noId = Template("no id", Now.AddHours(-1), Guid.Empty);
        var remote = new LayoutTemplateSyncState([good, null!, noId], [null!, Tombstone(Guid.Empty, Now)]);

        LayoutTemplateMergeResult result = LayoutTemplateMerge.Merge(LayoutTemplateSyncState.Empty, remote, Now);

        Assert.AreEqual("good", result.Merged.Templates.Single().Name);
        Assert.IsEmpty(result.Merged.Tombstones);
    }
}
