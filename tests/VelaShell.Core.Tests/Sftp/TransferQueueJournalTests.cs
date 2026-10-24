using System.Collections.Concurrent;
using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;

namespace VelaShell.Core.Tests.Sftp;

/// <summary>
/// 传输队列持久化的账本:登记、落定、作废、分段落盘、跨重启读回。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public sealed class TransferQueueJournalTests
{
    private static readonly TransferQueueOrigin Origin = new(Guid.NewGuid(), "prod-01");

    private static List<TransferQueueItem> Items(int count, TransferType type = TransferType.Upload) =>
    [
        .. Enumerable.Range(0, count).Select(i => new TransferQueueItem(type, $"C:/data/f{i}.bin", $"/srv/f{i}.bin")),
    ];

    [TestMethod]
    public async Task ABatchSurvivesARestart_WithOnlyTheItemsNotYetSettled()
    {
        var store = new JsonStore();
        var before = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid id = before.Begin(Origin, Items(5));
        before.Settle(id, 0);
        before.Settle(id, 3);
        await before.FlushAsync();

        var after = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        IReadOnlyList<PausedTransferBatch> restored = await after.LoadAsync();

        PausedTransferBatch batch = restored.Single();
        Assert.AreEqual(id, batch.BatchId);
        Assert.AreEqual(Origin, batch.Origin);
        CollectionAssert.AreEqual(
            new[] { "/srv/f1.bin", "/srv/f2.bin", "/srv/f4.bin" },
            batch.Items.Select(i => i.RemotePath).ToArray(),
            "落定的项不再出现,剩下的保持原来的先后");
        Assert.IsTrue(batch.Items.All(i => i.Type == TransferType.Upload));
    }

    [TestMethod]
    public async Task DirectionIsStoredAsText_SoDownloadsNeverComeBackAsUploads()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        journal.Begin(Origin, Items(1, TransferType.Download));
        await journal.FlushAsync();

        Assert.Contains("\"direction\":\"download\"", store.RawDocuments().Single());
        PausedTransferBatch batch = (await new TransferQueueJournal(store).LoadAsync()).Single();
        Assert.AreEqual(TransferType.Download, batch.Items.Single().Type);
    }

    [TestMethod]
    public async Task LargeBatches_AreStoredInChunks_AndOnlyChangedChunksAreRewritten()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid id = journal.Begin(Origin, Items(TransferQueueJournal.ChunkSize * 2 + 50));
        await journal.FlushAsync();
        Assert.AreEqual(3, store.Count, "450 项 = 3 段");

        store.ResetWrites();
        journal.Settle(id, 5);
        await journal.FlushAsync();
        Assert.AreEqual(1, store.Writes, "只重写第 0 段");

        for (int i = 0; i < TransferQueueJournal.ChunkSize; i++)
        {
            journal.Settle(id, i);
        }
        await journal.FlushAsync();
        Assert.AreEqual(2, store.Count, "第 0 段全部落定,那份文档删掉");
    }

    [TestMethod]
    public async Task DroppingABatch_RemovesEveryChunk()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        Guid id = journal.Begin(Origin, Items(TransferQueueJournal.ChunkSize + 1));
        await journal.FlushAsync();

        journal.Drop(id);
        await journal.FlushAsync();

        Assert.AreEqual(0, store.Count);
        Assert.IsNull(journal.Snapshot(id));
    }

    [TestMethod]
    public async Task ARestoredBatch_CanBeDropped_AndIsGoneAfterTheNextRestart()
    {
        var store = new JsonStore();
        var first = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        first.Begin(Origin, Items(3));
        await first.FlushAsync();

        var second = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        PausedTransferBatch restored = (await second.LoadAsync()).Single();
        Assert.AreEqual(3, second.Snapshot(restored.BatchId)?.Items.Count, "读回来的批次照样记在账上");
        second.Drop(restored.BatchId);
        await second.FlushAsync();

        Assert.IsEmpty(await new TransferQueueJournal(store).LoadAsync());
    }

    [TestMethod]
    public async Task UnreadableEntries_AreSkipped_AndEmptyBatchesCleanedUp()
    {
        var store = new JsonStore();
        var good = Guid.NewGuid();
        var hollow = Guid.NewGuid();
        await store.UpsertAsync(TransferQueueJournal.Collection, "good", new TransferQueueChunk
        {
            BatchId = good,
            ProfileId = Origin.ProfileId,
            ServerName = "prod-01",
            Items =
            [
                new() { Index = 0, Direction = "upload", LocalPath = "C:/a", RemotePath = "/a" },
                new() { Index = 1, Direction = "sideways", LocalPath = "C:/b", RemotePath = "/b" },
                new() { Index = 2, Direction = "download", LocalPath = "", RemotePath = "/c" },
            ],
        });
        await store.UpsertAsync(TransferQueueJournal.Collection, TransferQueueJournal.DocumentId(hollow, 0), new TransferQueueChunk
        {
            BatchId = hollow,
            Items = [new() { Index = 0, Direction = "?", LocalPath = "C:/x", RemotePath = "/x" }],
        });

        var journal = new TransferQueueJournal(store, TimeSpan.FromHours(1));
        IReadOnlyList<PausedTransferBatch> restored = await journal.LoadAsync();
        await journal.FlushAsync();

        Assert.AreEqual("/a", restored.Single().Items.Single().RemotePath);
        Assert.IsFalse(store.Contains(TransferQueueJournal.DocumentId(hollow, 0)), "一项都读不出的批次顺手删掉");
    }

    [TestMethod]
    public void OnlyUploadsAndDownloadsArePersisted()
    {
        var journal = new TransferQueueJournal(null);

        Assert.IsTrue(TransferQueueJournal.CanPersist(TransferType.Upload));
        Assert.IsTrue(TransferQueueJournal.CanPersist(TransferType.Download));
        Assert.IsFalse(TransferQueueJournal.CanPersist(TransferType.Relay), "中转要两条连接同时在才能接着传");
        Assert.IsFalse(TransferQueueJournal.CanPersist(TransferType.Copy));
        Assert.ThrowsExactly<ArgumentException>(() =>
            journal.Begin(Origin, [new TransferQueueItem(TransferType.Relay, "/src/a", "/dst/a")]));
    }

    [TestMethod]
    public async Task WithoutAStore_TheJournalStillKeepsTheBooksInMemory()
    {
        var journal = new TransferQueueJournal(null);
        Guid id = journal.Begin(Origin, Items(2));
        journal.Settle(id, 1);
        await journal.FlushAsync();

        Assert.AreEqual("/srv/f0.bin", journal.Snapshot(id)!.Items.Single().RemotePath);
        Assert.IsEmpty(await journal.LoadAsync());
    }

    [TestMethod]
    public void ABatchWhoseItemsAreAllSettled_HasNothingToResume()
    {
        var journal = new TransferQueueJournal(null);
        Guid id = journal.Begin(Origin, Items(2));
        journal.Settle(id, 0);
        journal.Settle(id, 1);
        journal.Settle(id, 1);

        Assert.IsNull(journal.Snapshot(id));
    }

    [TestMethod]
    public async Task ChangesAreFlushedByThemselves_AfterTheDelay()
    {
        var store = new JsonStore();
        var journal = new TransferQueueJournal(store, TimeSpan.FromMilliseconds(20));
        journal.Begin(Origin, Items(1));

        for (int i = 0; i < 200 && store.Count == 0; i++)
        {
            await Task.Delay(25);
        }

        Assert.AreEqual(1, store.Count, "不调 FlushAsync 也会在攒够延迟之后自己落盘");
    }

    /// <summary>按 JSON 存取的内存文档库(与 SonnetDB 一样经过一次序列化,不会把对象引用原样交回)。</summary>
    private sealed class JsonStore : IAppDataStore
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly ConcurrentDictionary<(string Collection, string Id), string> _docs = new();
        private int _writes;

        public int Count => _docs.Count;

        public int Writes => Volatile.Read(ref _writes);

        public void ResetWrites() => Interlocked.Exchange(ref _writes, 0);

        public bool Contains(string id) => _docs.Keys.Any(k => k.Id == id);

        public IEnumerable<string> RawDocuments() => _docs.Values;

        public Task<T?> GetAsync<T>(string collection, string id, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_docs.TryGetValue((collection, id), out string? json) ? JsonSerializer.Deserialize<T>(json, Json) : null);

        public Task<List<T>> GetAllAsync<T>(string collection, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_docs.Where(d => d.Key.Collection == collection)
                .Select(d => JsonSerializer.Deserialize<T>(d.Value, Json)!)
                .ToList());

        public Task UpsertAsync<T>(string collection, string id, T value, CancellationToken cancellationToken = default) where T : class
        {
            _docs[(collection, id)] = JsonSerializer.Serialize(value, Json);
            Interlocked.Increment(ref _writes);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
        {
            _docs.TryRemove((collection, id), out _);
            Interlocked.Increment(ref _writes);
            return Task.CompletedTask;
        }
    }
}
