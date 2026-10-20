using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;

namespace VelaShell.Presentation.Tests.Services;

/// <summary>
/// 共享凭据(#550)与连接之间的批量操作:挂上、摘下(凭据拷回)、保存编辑框、删除、迁移匹配。
/// </summary>
[TestClass]
public sealed class SharedCredentialServiceTests
{
    private readonly MemorySessions _sessions = new();
    private readonly MemoryCredentials _credentials = new();
    private readonly SharedCredentialService _service;

    public SharedCredentialServiceTests() => _service = new(_credentials, _sessions);

    private static SharedCredential PasswordCredential(string username = "admin") =>
        new() { Name = "switches", Username = username, Password = "s3cret" };

    private SessionProfile Add(SessionProfile profile)
    {
        _sessions.Store[profile.Id] = profile.Clone();
        return profile;
    }

    [TestMethod]
    public async Task Attach_ReferencesTheCredential_DropsOwnMaterial_AndFollowsItsUsername()
    {
        SharedCredential credential = PasswordCredential();
        SessionProfile same = Add(new() { Name = "a", Host = "a", Username = "admin", Password = "old" });
        SessionProfile other = Add(new() { Name = "b", Host = "b", Username = "operator", Password = "old" });

        await _service.AttachAsync(credential, [same.Id, other.Id]);

        SessionProfile a = _sessions.Store[same.Id];
        Assert.AreEqual(CredentialReference.ForShared(credential.Id), a.CredentialSource);
        Assert.IsNull(a.Password);
        // 与凭据相同的用户名清掉,让它跟着凭据走;不同的保留(那是有意的覆盖)。
        Assert.AreEqual(string.Empty, a.Username);
        Assert.AreEqual("operator", _sessions.Store[other.Id].Username);
    }

    [TestMethod]
    public async Task Attach_SkipsConnectionsThatCannotUseTheCredential()
    {
        var key = new SharedCredential { Name = "key", Username = "root", AuthMethod = AuthMethod.PrivateKey, PrivateKeyPath = "/k" };
        SessionProfile ftp = Add(new() { Name = "ftp", Host = "f", ConnectionType = ConnectionType.FTP, Username = "u", Password = "p" });

        await _service.AttachAsync(key, [ftp.Id]);

        Assert.IsNull(_sessions.Store[ftp.Id].CredentialSource);
        Assert.AreEqual("p", _sessions.Store[ftp.Id].Password);
    }

    /// <summary>摘下凭据时把它拷回连接:用户只是不想跟着凭据走,不是想让这条连接连不上。</summary>
    [TestMethod]
    public async Task Detach_CopiesTheCredentialBack()
    {
        SharedCredential credential = PasswordCredential();
        SessionProfile inherits = Add(new() { Name = "a", Host = "a", CredentialSource = CredentialReference.ForShared(credential.Id), RememberPassword = false });
        SessionProfile overrides = Add(new() { Name = "b", Host = "b", Username = "operator", CredentialSource = CredentialReference.ForShared(credential.Id) });

        await _service.DetachAsync(credential, [inherits.Id, overrides.Id]);

        SessionProfile a = _sessions.Store[inherits.Id];
        Assert.IsNull(a.CredentialSource);
        Assert.AreEqual("admin", a.Username);
        Assert.AreEqual("s3cret", a.Password);
        Assert.IsTrue(a.RememberPassword);
        Assert.AreEqual("operator", _sessions.Store[overrides.Id].Username);
    }

    [TestMethod]
    public async Task Save_AttachesAddedUsers_DetachesRemovedOnes_ThenStoresTheCredential()
    {
        SharedCredential credential = PasswordCredential();
        SessionProfile kept = Add(new() { Name = "kept", Host = "k", CredentialSource = CredentialReference.ForShared(credential.Id) });
        SessionProfile removed = Add(new() { Name = "removed", Host = "r", CredentialSource = CredentialReference.ForShared(credential.Id) });
        SessionProfile added = Add(new() { Name = "added", Host = "n", Username = "admin", Password = "x" });
        var order = new List<string>();
        _sessions.Saved += () => order.Add("profile");
        _credentials.Saved += () => order.Add("credential");

        await _service.SaveAsync(credential, [kept.Id, added.Id]);

        Assert.IsNotNull(_sessions.Store[kept.Id].CredentialSource);
        Assert.IsNotNull(_sessions.Store[added.Id].CredentialSource);
        Assert.IsNull(_sessions.Store[removed.Id].CredentialSource);
        Assert.AreEqual("s3cret", _sessions.Store[removed.Id].Password);
        Assert.IsTrue(_credentials.Store.ContainsKey(credential.Id));
        // 凭据最后存:它的 Changed 是界面刷新的信号,那一刻连接已经改完。
        Assert.AreEqual("credential", order[^1]);
    }

    /// <summary>把密码换成私钥之后,FTP 用不上它了:解除并拷回,而不是留一条注定解析失败的引用。</summary>
    [TestMethod]
    public async Task Save_DetachesUsersThatCanNoLongerUseTheCredential()
    {
        var credential = new SharedCredential { Name = "c", Username = "root", AuthMethod = AuthMethod.PrivateKey, PrivateKeyPath = "/k" };
        SessionProfile ftp = Add(new() { Name = "ftp", Host = "f", ConnectionType = ConnectionType.FTP, CredentialSource = CredentialReference.ForShared(credential.Id) });

        await _service.SaveAsync(credential, [ftp.Id]);

        Assert.IsNull(_sessions.Store[ftp.Id].CredentialSource);
    }

    [TestMethod]
    public async Task Delete_LeavesEveryUserWithItsOwnCopy()
    {
        SharedCredential credential = PasswordCredential();
        _credentials.Store[credential.Id] = credential;
        SessionProfile user = Add(new() { Name = "a", Host = "a", CredentialSource = CredentialReference.ForShared(credential.Id) });

        await _service.DeleteAsync(credential);

        Assert.IsFalse(_credentials.Store.ContainsKey(credential.Id));
        Assert.IsNull(_sessions.Store[user.Id].CredentialSource);
        Assert.AreEqual("s3cret", _sessions.Store[user.Id].Password);
    }

    [TestMethod]
    public async Task CountUsages_CountsReferencesPerCredential()
    {
        var a = Guid.NewGuid();
        Add(new() { Name = "1", Host = "1", CredentialSource = CredentialReference.ForShared(a) });
        Add(new() { Name = "2", Host = "2", CredentialSource = CredentialReference.ForShared(a) });
        Add(new() { Name = "3", Host = "3" });

        IReadOnlyDictionary<Guid, int> usages = await _service.CountUsagesAsync();

        Assert.AreEqual(2, usages[a]);
        Assert.HasCount(1, usages);
    }

    [TestMethod]
    public void HasSameCredential_MatchesStandaloneConnectionsWithIdenticalMaterial()
    {
        SharedCredential credential = PasswordCredential();

        Assert.IsTrue(SharedCredentialService.HasSameCredential(credential,
            new() { Username = "admin", Password = "s3cret" }));
        Assert.IsFalse(SharedCredentialService.HasSameCredential(credential,
            new() { Username = "admin", Password = "other" }));
        Assert.IsFalse(SharedCredentialService.HasSameCredential(credential,
            new() { Username = "root", Password = "s3cret" }), "凭据带了用户名时,用户名也要相同");
        Assert.IsFalse(SharedCredentialService.HasSameCredential(credential,
            new() { Username = "admin", Password = "s3cret", CredentialSource = CredentialReference.ForShared(Guid.NewGuid()) }),
            "已经引用凭据的连接不算");
        // 不带用户名的凭据本来就是给不同登录名共用的:只比材料。
        Assert.IsTrue(SharedCredentialService.HasSameCredential(PasswordCredential(username: string.Empty),
            new() { Username = "whoever", Password = "s3cret" }));
        // Agent 没有材料可比:匹配出来的只会是一堆不相干的连接。
        Assert.IsFalse(SharedCredentialService.HasSameCredential(
            new SharedCredential { Name = "a", AuthMethod = AuthMethod.Agent },
            new() { AuthMethod = AuthMethod.Agent }));
    }

    [TestMethod]
    public void IsCompatible_FollowsTheProtocol()
    {
        var key = new SharedCredential { AuthMethod = AuthMethod.PrivateKey };
        SharedCredential password = PasswordCredential();

        Assert.IsTrue(SharedCredentialService.IsCompatible(key, new() { ConnectionType = ConnectionType.SFTP }));
        Assert.IsFalse(SharedCredentialService.IsCompatible(key, new() { ConnectionType = ConnectionType.Plugin }));
        Assert.IsTrue(SharedCredentialService.IsCompatible(password, new() { ConnectionType = ConnectionType.FTP }));
        Assert.IsFalse(SharedCredentialService.IsCompatible(password,
            new() { ConnectionType = ConnectionType.FTP, Ftp = new() { Anonymous = true } }));
    }

    /// <summary>与仓储同口径的内存实现:读出、存入都是副本。</summary>
    private sealed class MemorySessions : ISessionRepository
    {
        public Dictionary<Guid, SessionProfile> Store { get; } = [];

        public event Action? Saved;

        public Task<List<ServerGroup>> GetAllGroupsAsync() => Task.FromResult(new List<ServerGroup>());

        public Task<List<SessionProfile>> GetAllSessionsAsync() =>
            Task.FromResult(Store.Values.Select(static p => p.Clone()).ToList());

        public Task<SessionProfile?> GetSessionAsync(Guid id) =>
            Task.FromResult(Store.TryGetValue(id, out SessionProfile? p) ? p.Clone() : null);

        public Task SaveSessionAsync(SessionProfile session)
        {
            Store[session.Id] = session.Clone();
            Saved?.Invoke();
            return Task.CompletedTask;
        }

        public Task ReplaceSessionAsync(SessionProfile session) => SaveSessionAsync(session);

        public Task<List<RemoteDirectoryBookmark>?> UpdateDirectoryBookmarksAsync(
            Guid id, Func<List<RemoteDirectoryBookmark>, bool> update) =>
            throw new NotSupportedException("凭据服务不碰目录收藏。");

        public Task DeleteSessionAsync(Guid id)
        {
            Store.Remove(id);
            return Task.CompletedTask;
        }

        public Task SaveGroupAsync(ServerGroup group) => Task.CompletedTask;

        public Task DeleteGroupAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class MemoryCredentials : ISharedCredentialRepository
    {
        public Dictionary<Guid, SharedCredential> Store { get; } = [];

        public event EventHandler? Changed;

        public event Action? Saved;

        public Task<List<SharedCredential>> GetAllAsync() =>
            Task.FromResult(Store.Values.Select(static c => c.Clone()).ToList());

        public Task<List<SharedCredentialMetadata>> ListMetadataAsync() =>
            Task.FromResult(Store.Values.Select(c => new SharedCredentialMetadata(c.Id, c.Name, c.Username, c.AuthMethod, !string.IsNullOrEmpty(c.Password)))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());

        public Task<string?> GetPasswordAsync(Guid id) =>
            Task.FromResult(Store.TryGetValue(id, out SharedCredential? c) && c.AuthMethod == AuthMethod.Password ? c.Password : null);

        public Task<SharedCredential?> GetAsync(Guid id) =>
            Task.FromResult(Store.TryGetValue(id, out SharedCredential? c) ? c.Clone() : null);

        public Task SaveAsync(SharedCredential credential)
        {
            Store[credential.Id] = credential.Clone();
            Saved?.Invoke();
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id)
        {
            Store.Remove(id);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
