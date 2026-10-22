using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

/// <summary>GitHubReleaseSource 的更新日志、清单与断点续传行为(经注入的 HttpMessageHandler 打桩)。</summary>
[TestClass]
public class GitHubReleaseSourceTests : IDisposable
{
    private readonly string _dir;

    public GitHubReleaseSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"velashell_ghsource_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
        GC.SuppressFinalize(this);
    }

    private static UpdateManifest CreateManifest(string assetName, long size) =>
        UpdateManifest.Parse($$"""
            { "version": "2.0.0", "tag": "v2.0.0",
              "assets": { "test-rid": { "name": "{{assetName}}", "sha256": "unused", "size": {{size}} } } }
            """);

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_NoPartial_DownloadsWholeFileWithoutRangeHeader()
    {
        byte[] payload = Encoding.ASCII.GetBytes("full-content");
        RangeHeaderValue? seenRange = null;
        StubHandler handler = new(request =>
        {
            seenRange = request.Headers.Range;
            return FullResponse(payload);
        });
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);
        string destination = Path.Combine(_dir, "pkg.zip");

        string? hash = await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.IsNull(seenRange);
        Assert.AreEqual("full-content", await File.ReadAllTextAsync(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"), "下载完成后不应残留半成品");
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), hash, "返回的流式哈希应与内容一致");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_ReportsBytesAndTotalAlongsidePercent()
    {
        byte[] payload = Encoding.ASCII.GetBytes(string.Join('-', Enumerable.Range(0, 200_000)));
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ => FullResponse(payload)));
        List<UpdateDownloadProgress> reports = [];

        await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length),
            Asset(payload.Length),
            Path.Combine(_dir, "pkg.zip"),
            new ByteProgress(reports.Add));

        Assert.IsNotEmpty(reports);
        // 收尾必须是「已下满整包」的快照,界面据此从速率切到可重启状态。
        UpdateDownloadProgress last = reports[^1];
        Assert.AreEqual(100, last.Percent);
        Assert.AreEqual(payload.LongLength, last.BytesDownloaded);
        Assert.AreEqual(payload.LongLength, last.TotalBytes);
        Assert.IsTrue(
            reports.All(r => r.TotalBytes == payload.LongLength),
            "每条汇报都要带得上整包字节数,界面才能显示「已下载/总量」。");
        Assert.IsTrue(
            reports.Select(r => r.BytesDownloaded).SequenceEqual(reports.Select(r => r.BytesDownloaded).Order()),
            "字节数只能单调不减。");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_UnknownTotal_StillReportsBytes()
    {
        // 清单没写大小、响应也不带 Content-Length:老实现一条进度都不报,界面整段都以为卡住了,
        // 停滞看门狗也会把一个好好的下载当成断线掐掉。
        byte[] payload = Encoding.ASCII.GetBytes(string.Join('-', Enumerable.Range(0, 60_000)));
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
        {
            HttpResponseMessage response = FullResponse(payload);
            response.Content.Headers.ContentLength = null;
            return response;
        }));
        List<UpdateDownloadProgress> reports = [];

        await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", 0), Asset(0), Path.Combine(_dir, "pkg.zip"), new ByteProgress(reports.Add));

        Assert.Contains(r => r.TotalBytes == 0 && r.BytesDownloaded > 0 && r.BytesDownloaded < payload.Length, reports);
        Assert.AreEqual(payload.LongLength, reports[^1].BytesDownloaded, "收尾快照报的是实际下到的字节数");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_WithPartial_SendsRangeAndAppends()
    {
        byte[] payload = Encoding.ASCII.GetBytes("0123456789");
        string destination = Path.Combine(_dir, "pkg.zip");
        await File.WriteAllTextAsync(destination + ".partial", "01234");
        RangeHeaderValue? seenRange = null;
        StubHandler handler = new(request =>
        {
            seenRange = request.Headers.Range;
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            HttpResponseMessage response = new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[(int)from..])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            return response;
        });
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);

        List<int> reported = [];
        await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination,
            new SynchronousProgress(reported.Add));

        Assert.AreEqual(5, seenRange?.Ranges.First().From);
        Assert.AreEqual("0123456789", await File.ReadAllTextAsync(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"));
        Assert.Contains(100, reported);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_ServerIgnoresRange_RestartsFromScratch()
    {
        byte[] payload = Encoding.ASCII.GetBytes("full-content");
        string destination = Path.Combine(_dir, "pkg.zip");
        await File.WriteAllTextAsync(destination + ".partial", "wrong");
        StubHandler handler = new(_ => FullResponse(payload));
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);

        await source.DownloadAssetAsync(CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.AreEqual("full-content", await File.ReadAllTextAsync(destination));
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_RangeNotSatisfiable_RetriesWholeFile()
    {
        byte[] payload = Encoding.ASCII.GetBytes("full-content");
        string destination = Path.Combine(_dir, "pkg.zip");
        // 半成品长度声称 5,但服务器上的资产已变化,Range 请求得到 416。
        await File.WriteAllTextAsync(destination + ".partial", "abcde");
        int requests = 0;
        StubHandler handler = new(request =>
        {
            requests++;
            return request.Headers.Range != null
                ? new(HttpStatusCode.RequestedRangeNotSatisfiable)
                : FullResponse(payload);
        });
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);

        await source.DownloadAssetAsync(CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.AreEqual(2, requests);
        Assert.AreEqual("full-content", await File.ReadAllTextAsync(destination));
    }

    // ———— 分段并发下载(≥16MB 的包) ————

    /// <summary>可复现的 24MB 伪随机内容(24MB / 8MB 段宽 = 3 段并发)。</summary>
    private static byte[] LargePayload()
    {
        byte[] payload = new byte[24 * 1024 * 1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 + 7);
        }
        return payload;
    }

    /// <summary>按请求的 Range 头切片返回 206 的打桩;无 Range 时返回 200 整包。线程安全,记录所有请求区间。</summary>
    private static StubHandler SlicingHandler(byte[] payload, List<(long From, long? To)> rangeLog)
    {
        return new(request =>
        {
            RangeItemHeaderValue? range = request.Headers.Range?.Ranges.First();
            lock (rangeLog)
            {
                if (range != null)
                {
                    rangeLog.Add((range.From!.Value, range.To));
                }
            }
            if (range == null)
            {
                return FullResponse(payload);
            }
            long from = range.From!.Value;
            long to = range.To ?? payload.Length - 1;
            HttpResponseMessage response = new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[(int)from..(int)(to + 1)])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
            return response;
        });
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_LargeAsset_DownloadsInParallelSegments()
    {
        byte[] payload = LargePayload();
        List<(long From, long? To)> ranges = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", SlicingHandler(payload, ranges));
        string destination = Path.Combine(_dir, "pkg.zip");

        string? hash = await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.HasCount(3, ranges, "24MB 应切成 3 段并发请求");
        Assert.AreSequenceEqual(
            [0, 8 * 1024 * 1024, 16 * 1024 * 1024], ranges.Select(r => r.From).ToArray(), SequenceOrder.InAnyOrder);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), hash);
        Assert.AreEqual(payload.Length, new FileInfo(destination).Length);
        byte[] downloaded = await File.ReadAllBytesAsync(destination);
        Assert.IsTrue(payload.AsSpan().SequenceEqual(downloaded), "分段拼装后的内容必须逐字节一致");
        Assert.IsFalse(File.Exists(destination + ".partial"));
        Assert.IsFalse(File.Exists(destination + ".partial.meta"), "完成后应清掉分段断点元数据");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_SegmentedResume_SkipsCompletedSegments()
    {
        byte[] payload = LargePayload();
        const long segmentLength = 8 * 1024 * 1024;
        string destination = Path.Combine(_dir, "pkg.zip");
        string partial = destination + ".partial";
        // 伪造上次中断的现场:第一段已完成(数据在盘上),后两段未开始。
        byte[] partialBytes = new byte[payload.Length];
        Array.Copy(payload, partialBytes, segmentLength);
        await File.WriteAllBytesAsync(partial, partialBytes);
        await File.WriteAllTextAsync(partial + ".meta", $$"""
            {
              "Sha256": "unused",
              "Size": {{payload.Length}},
              "Segments": [
                { "Start": 0, "Length": {{segmentLength}}, "Done": {{segmentLength}} },
                { "Start": {{segmentLength}}, "Length": {{segmentLength}}, "Done": 0 },
                { "Start": {{2 * segmentLength}}, "Length": {{segmentLength}}, "Done": 0 }
              ]
            }
            """);
        List<(long From, long? To)> ranges = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", SlicingHandler(payload, ranges));

        string? hash = await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.HasCount(2, ranges, "已完成的第一段不应重新请求");
        Assert.AreSequenceEqual(
            [segmentLength, 2 * segmentLength], [.. ranges.Select(r => r.From)], SequenceOrder.InAnyOrder);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), hash, "续传拼装后的整包哈希必须正确");
        byte[] downloaded = await File.ReadAllBytesAsync(destination);
        Assert.IsTrue(payload.AsSpan().SequenceEqual(downloaded));
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_LargeAssetServerIgnoresRange_FallsBackToSequential()
    {
        byte[] payload = LargePayload();
        int requests = 0;
        StubHandler handler = new(_ =>
        {
            Interlocked.Increment(ref requests);
            // 无论是否带 Range 一律回 200 整包:分段方案应作废并退回单连接。
            return FullResponse(payload);
        });
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);
        string destination = Path.Combine(_dir, "pkg.zip");

        string? hash = await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), hash);
        byte[] downloaded = await File.ReadAllBytesAsync(destination);
        Assert.IsTrue(payload.AsSpan().SequenceEqual(downloaded));
        Assert.IsFalse(File.Exists(destination + ".partial.meta"));
    }

    [TestMethod]
    public async Task ReleaseNotes_ReadsLatestStableManifest_WithoutCallingApiOrRequiringAssets()
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            calls++;
            Assert.AreEqual("https://github.com/owner/repo/releases/latest/download/latest.json", request.RequestUri!.AbsoluteUri);
            Assert.IsNotEmpty(request.Headers.UserAgent);
            Assert.IsEmpty(request.Headers.Accept);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "tag":"v1.7.1",
                      "releaseNotes": {
                        "body":"## What's Changed\n* 更新下载 by @joesdu in #626\n\n完整正文",
                        "publishedAt":"2026-10-16T00:00:00Z",
                        "htmlUrl":"https://github.com/owner/repo/releases/tag/v1.7.1"
                      }
                    }
                    """)
            };
        }));

        ReleaseNotes? notes = await source.GetLatestReleaseNotesAsync();

        Assert.IsNotNull(notes);
        Assert.AreEqual("v1.7.1", notes.Tag);
        Assert.AreEqual("## What's Changed\n* 更新下载 by @joesdu in #626\n\n完整正文", notes.Body);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-16T00:00:00Z"), notes.PublishedAt);
        Assert.AreEqual("https://github.com/owner/repo/releases/tag/v1.7.1", notes.HtmlUrl);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ReleaseNotes_CachedInProcess_ThenRevalidatedWithETag()
    {
        const string json = """{"tag":"v1.7.1","releaseNotes":{"body":"notes","publishedAt":"2026-10-16T00:00:00Z"}}""";
        List<string?> ifNoneMatch = [];
        StubHandler handler = new(request =>
        {
            ifNoneMatch.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.ToString());
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                return new(HttpStatusCode.NotModified);
            }
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(json) };
            response.Headers.ETag = new EntityTagHeaderValue("\"abc\"", isWeak: true);
            return response;
        });

        // 缓存期内反复打开更新日志:只下载一次清单。
        GitHubReleaseSource cached = new("https://github.com/owner/repo", handler);
        ReleaseNotes? first = await cached.GetLatestReleaseNotesAsync();
        Assert.AreEqual(first, await cached.GetLatestReleaseNotesAsync());
        Assert.HasCount(1, ifNoneMatch);

        // 缓存过期:带 ETag 做条件请求,304 时沿用手里的结果。
        ifNoneMatch.Clear();
        GitHubReleaseSource revalidating = new("https://github.com/owner/repo", handler)
        {
            ReleaseNotesCacheDuration = TimeSpan.Zero
        };
        ReleaseNotes? fresh = await revalidating.GetLatestReleaseNotesAsync();
        ReleaseNotes? revalidated = await revalidating.GetLatestReleaseNotesAsync();

        Assert.AreSequenceEqual([null, "W/\"abc\""], ifNoneMatch);
        Assert.IsNotNull(revalidated);
        Assert.AreEqual(fresh, revalidated);
        Assert.AreEqual("v1.7.1", revalidated.Tag);
    }

    [TestMethod]
    public async Task Manifest_StableChannel_UsesAttachmentWithoutApi()
    {
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            Assert.AreEqual("https://github.com/owner/repo/releases/latest/download/latest.json", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"version":"1.7.1","tag":"v1.7.1","assets":{"win-x64":{"name":"pkg.zip","sha256":"abc","size":12}},"releaseNotes":{"body":"notes"}}
                """) };
        }));

        UpdateManifest? manifest = await source.GetLatestManifestAsync(includePreRelease: false);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("v1.7.1", manifest.Tag);
        Assert.AreEqual("pkg.zip", manifest.Assets["win-x64"].Name);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Manifest_ApiFallbackAndPreviewChannel_KeepExistingSelection(bool preview)
    {
        List<string> urls = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            urls.Add(url);
            if (url == "https://github.com/owner/repo/releases/latest/download/latest.json")
            {
                return new(HttpStatusCode.NotFound);
            }
            if (request.RequestUri.Host == "api.github.com")
            {
                return new(HttpStatusCode.OK) { Content = new StringContent("""
                    [
                      {"draft":true},
                      {"draft":false,"prerelease":true,"tag_name":"v2.0-beta"},
                      {"draft":false,"prerelease":false,"tag_name":"v1.7.1"}
                    ]
                    """) };
            }
            string tag = preview ? "v2.0-beta" : "v1.7.1";
            Assert.AreEqual($"https://github.com/owner/repo/releases/download/{tag}/latest.json", url);
            return new(HttpStatusCode.OK) { Content = new StringContent($$$"""
                {"version":"{{{tag[1..]}}}","tag":"{{{tag}}}","assets":{"win-x64":{"name":"pkg.zip","sha256":"abc"} } }
                """) };
        }));

        UpdateManifest? manifest = await source.GetLatestManifestAsync(preview);
        Assert.AreEqual(preview ? "v2.0-beta" : "v1.7.1", manifest!.Tag);
        Assert.HasCount(preview ? 2 : 3, urls);
        Assert.Contains("https://api.github.com/repos/owner/repo/releases?per_page=20", urls);
    }

    [TestMethod]
    public void ApiHandler_AcceptsCompressedResponses()
    {
        // 发布列表 JSON 不压缩时 53 个发布就有 1.2 MB,gzip 后约 120 KB。
        using SocketsHttpHandler handler = GitHubReleaseSource.CreateApiHandler();
        Assert.IsTrue(handler.AutomaticDecompression.HasFlag(DecompressionMethods.GZip));
        Assert.IsTrue(handler.AutomaticDecompression.HasFlag(DecompressionMethods.Brotli));
    }

    [TestMethod]
    public async Task ReleaseNotes_InternalTimeout_FailsInsteadOfHanging()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GitHubReleaseSource source = new("https://github.com/owner/repo", new CancellableHandler(started))
        {
            RequestTimeout = TimeSpan.FromMilliseconds(100)
        };

        // 调用方的令牌一直没取消:超时由发布源自己掐断,以取消异常的形式交给界面,界面据此进入可重试的错误态。
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetLatestReleaseNotesAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.IsTrue(started.Task.IsCompleted);
    }

    [TestMethod]
    public async Task ReleaseNotes_MissingStableManifest_CachesNullWithoutApiFallback()
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            calls++;
            Assert.AreEqual("https://github.com/owner/repo/releases/latest/download/latest.json", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.NotFound);
        }));
        Assert.IsNull(await source.GetLatestReleaseNotesAsync());
        Assert.IsNull(await source.GetLatestReleaseNotesAsync());
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow("{\"tag\":\"v1.7.1\"}")]
    [DataRow("{\"tag\":\"v1.7.1\",\"releaseNotes\":null}")]
    [DataRow("{\"tag\":\"v1.7.1\",\"releaseNotes\":{}}")]
    [DataRow("{\"tag\":\"v1.7.1\",\"releaseNotes\":{\"body\":null}}")]
    [DataRow("{\"tag\":\"v1.7.1\",\"releaseNotes\":{\"body\":\"\",\"publishedAt\":null,\"htmlUrl\":null}}")]
    public async Task ReleaseNotes_OldManifestOrEmptyBody_PreservesVersion(string json)
    {
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        Assert.AreEqual(new ReleaseNotes("v1.7.1", ""), await source.GetLatestReleaseNotesAsync());
    }

    [TestMethod]
    public async Task ReleaseNotes_NullBody_PreservesPublication()
    {
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent("""
                {"tag":"v1.7.1","releaseNotes":{"body":null,"publishedAt":"2026-10-16T00:00:00Z"}}
                """) }));
        Assert.AreEqual(new ReleaseNotes("v1.7.1", "", DateTimeOffset.Parse("2026-10-16T00:00:00Z")),
            await source.GetLatestReleaseNotesAsync());
    }

    [TestMethod]
    public async Task ReleaseNotes_ChangedManifest_ReplacesBodyAndClearsOldETag()
    {
        List<string?> etags = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            etags.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.ToString());
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(etags.Count == 1
                    ? """{"tag":"v1.0.0","releaseNotes":{"body":"old"}}"""
                    : """{"tag":"v2.0.0","releaseNotes":{"body":"new"}}""")
            };
            if (etags.Count == 1)
            {
                response.Headers.ETag = new EntityTagHeaderValue("\"old\"");
            }
            return response;
        }))
        { ReleaseNotesCacheDuration = TimeSpan.Zero };

        Assert.AreEqual("old", (await source.GetLatestReleaseNotesAsync())!.Body);
        Assert.AreEqual(new ReleaseNotes("v2.0.0", "new"), await source.GetLatestReleaseNotesAsync());
        await source.GetLatestReleaseNotesAsync();
        Assert.AreSequenceEqual([null, "\"old\"", null], etags);
    }

    [TestMethod]
    [DataRow("{}", typeof(KeyNotFoundException))]
    [DataRow("{\"tag\":null}", typeof(FormatException))]
    [DataRow("{\"tag\":\" \"}", typeof(FormatException))]
    [DataRow("{\"tag\":4}", typeof(InvalidOperationException))]
    [DataRow("{\"tag\":\"v1.0.0\",\"releaseNotes\":[]}", typeof(InvalidOperationException))]
    [DataRow("{\"tag\":\"v1.0.0\",\"releaseNotes\":{\"body\":3}}", typeof(InvalidOperationException))]
    [DataRow("{\"tag\":\"v1.0.0\",\"releaseNotes\":{\"publishedAt\":\"invalid\"}}", typeof(FormatException))]
    [DataRow("{\"tag\":\"v1.0.0\",\"releaseNotes\":{\"htmlUrl\":false}}", typeof(InvalidOperationException))]
    public async Task ReleaseNotes_InvalidManifest_Propagates(string json, Type exceptionType)
    {
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        Exception? error = null;
        try
        {
            await source.GetLatestReleaseNotesAsync();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        Assert.IsNotNull(error);
        Assert.AreEqual(exceptionType, error.GetType());
    }

    [TestMethod]
    [DataRow(403)]
    [DataRow(429)]
    [DataRow(500)]
    public async Task ReleaseNotes_HttpFailure_Propagates(int status)
    {
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ => new((HttpStatusCode)status)));
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetLatestReleaseNotesAsync());
        // 状态码要带出去:界面靠它把 403/429 限流和"检查网络"区分开。
        Assert.AreEqual((HttpStatusCode)status, ex.StatusCode);
    }

    [TestMethod]
    public async Task ReleaseNotes_FailedRefresh_DoesNotReplaceSuccessfulCache()
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                HttpResponseMessage first = new(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"tag":"v1.0.0","releaseNotes":{"body":"notes"}}""")
                };
                first.Headers.ETag = new EntityTagHeaderValue("\"original\"");
                return first;
            }
            Assert.AreEqual("\"original\"", request.Headers.IfNoneMatch.Single().ToString());
            return new(calls == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotModified);
        }))
        { ReleaseNotesCacheDuration = TimeSpan.Zero };

        ReleaseNotes? first = await source.GetLatestReleaseNotesAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetLatestReleaseNotesAsync());
        Assert.AreEqual(first, await source.GetLatestReleaseNotesAsync());
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task ReleaseNotes_InvalidJson_Propagates()
    {
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent("invalid") }));
        await Assert.ThrowsAsync<JsonException>(() => source.GetLatestReleaseNotesAsync());
    }

    [TestMethod]
    public async Task ReleaseNotes_CancelInFlight_PropagatesToHandler()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GitHubReleaseSource source = new("https://github.com/owner/repo", new CancellableHandler(started));
        using CancellationTokenSource cancellation = new();
        Task<ReleaseNotes?> pending = source.GetLatestReleaseNotesAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReleaseNotes_StalledResponseBody_RespectsTimeoutAndCallerCancellation(bool cancelCaller)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
            new(HttpStatusCode.OK) { Content = new StreamContent(new StalledReadStream(started)) }))
        {
            RequestTimeout = cancelCaller ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100)
        };

        Task<ReleaseNotes?> pending = source.GetLatestReleaseNotesAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow("1.7.1", true)]
    [DataRow("1.7.1.0", true)]
    [DataRow("1.7.1+build", true)]
    [DataRow("1.7.2", false)]
    [DataRow("1.7.1-beta.1", false)]
    [DataRow("invalid", false)]
    public async Task ReleaseNotes_ManifestCandidate_RequiresVersionMatchingRootTag(string version, bool eligible)
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
        {
            calls++;
            return NotesResponse("v1.7.1", version);
        }));

        ReleaseNotes? notes = await source.GetLatestReleaseNotesAsync();

        Assert.IsNotNull(notes);
        Assert.AreEqual("notes for v1.7.1", notes.Body);
        Assert.AreEqual(eligible, notes.Manifest != null);
        if (eligible)
        {
            Assert.AreEqual("v1.7.1", notes.Manifest!.Tag);
            Assert.AreEqual(version, notes.Manifest.Version);
            Assert.HasCount(1, notes.Manifest.Assets);
            Assert.AreEqual("pkg.zip", notes.Manifest.AssetForCurrentPlatform()!.Name);
        }
        Assert.AreEqual(1, calls, "日志和候选应来自同一响应。");
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("{\"name\":\"pkg.zip\"}")]
    [DataRow("{\"name\":null,\"sha256\":\"bad\",\"size\":1}")]
    [DataRow("{\"name\":\"../pkg.zip\",\"sha256\":\"bad\",\"size\":1}")]
    [DataRow("{\"name\":\"pkg.zip\",\"sha256\":\"bad\",\"size\":1}")]
    [DataRow("{\"name\":\"pkg.zip\",\"sha256\":\"bad\",\"size\":0}")]
    [DataRow("{\"name\":\"pkg.zip\",\"sha256\":\"bad\",\"size\":9223372036854775808}")]
    public async Task ReleaseNotes_BadCurrentPlatformAsset_PreservesBodyWithoutCandidate(string asset)
    {
        string json = $$"""
            {"version":"1.7.1","tag":"v1.7.1","assets":{"{{UpdateManifest.CurrentRid()}}":{{asset}} },
             "releaseNotes":{"body":"readable","publishedAt":"2026-10-21T00:00:00Z"} }
            """;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent(json) }));

        ReleaseNotes? notes = await source.GetLatestReleaseNotesAsync();

        Assert.IsNotNull(notes);
        Assert.AreEqual("readable", notes.Body);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-21T00:00:00Z"), notes.PublishedAt);
        Assert.IsNull(notes.Manifest);
    }

    [TestMethod]
    [DataRow("v1")]
    [DataRow("invalid")]
    [DataRow("v2.0.0-beta.1")]
    public async Task ReleaseNotes_Latest_RejectsInvalidOrPreviewTag(string tag)
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
        {
            calls++;
            return NotesResponse(tag, "2.0.0-beta.1");
        }));
        await Assert.ThrowsExactlyAsync<FormatException>(() => source.GetLatestReleaseNotesAsync());
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ReleaseNotes_Current_AllowsPreviewAndUsesExactTagWithoutApi()
    {
        List<string> urls = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            return NotesResponse("v2.0.0-beta.1", "2.0.0-beta.1");
        }));
        ReleaseNotes? notes = await source.GetReleaseNotesForVersionAsync("v2.0.0-beta.1+local");
        Assert.IsNotNull(notes);
        Assert.AreEqual("v2.0.0-beta.1", notes.Tag);
        Assert.IsNotNull(notes.Manifest);
        Assert.AreSequenceEqual(["https://github.com/owner/repo/releases/download/v2.0.0-beta.1/latest.json"], urls);
    }

    [TestMethod]
    [DataRow("v1.7.1", "v1.7.1|1.7.1")]
    [DataRow("1.7.1", "1.7.1|v1.7.1")]
    [DataRow("v1.7.1.0", "v1.7.1.0|1.7.1.0|v1.7.1|1.7.1")]
    [DataRow("1.7.1.0+local", "1.7.1.0|v1.7.1.0|1.7.1|v1.7.1")]
    [DataRow("v2.0.0.0-beta.1", "v2.0.0.0-beta.1|2.0.0.0-beta.1|v2.0.0-beta.1|2.0.0-beta.1")]
    public async Task ReleaseNotes_Current_Only404TriesFinitePrefixAndNormalizedVariants(string version, string expectedTags)
    {
        List<string> urls = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.NotFound);
        }));
        Assert.IsNull(await source.GetReleaseNotesForVersionAsync(version));
        Assert.IsNull(await source.GetReleaseNotesForVersionAsync(version));
        Assert.AreSequenceEqual([.. expectedTags.Split('|').Select(tag =>
            $"https://github.com/owner/repo/releases/download/{tag}/latest.json")], urls);
    }

    [TestMethod]
    public async Task ReleaseNotes_Current_NormalizedFallback_StopsAtFirstValidResponse()
    {
        List<string> urls = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            return urls.Count < 3 ? new(HttpStatusCode.NotFound) : NotesResponse("v1.7.1");
        }));
        ReleaseNotes? notes = await source.GetReleaseNotesForVersionAsync("v1.7.1.0");
        Assert.AreEqual("v1.7.1", notes!.Tag);
        Assert.HasCount(3, urls);
        Assert.AreEqual("https://github.com/owner/repo/releases/download/v1.7.1/latest.json", urls[^1]);
    }

    [TestMethod]
    [DataRow(403)]
    [DataRow(429)]
    [DataRow(500)]
    public async Task ReleaseNotes_Current_HttpFailure_DoesNotTryAnotherTag(int status)
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
        {
            calls++;
            return new((HttpStatusCode)status);
        }));
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetReleaseNotesForVersionAsync("v1.7.1.0"));
        Assert.AreEqual((HttpStatusCode)status, error.StatusCode);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ReleaseNotes_Current_InvalidJson_DoesNotTryAnotherTag()
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
        {
            calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent("invalid") };
        }));
        await Assert.ThrowsAsync<JsonException>(() => source.GetReleaseNotesForVersionAsync("v1.7.1.0"));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow("1.7.1")]
    [DataRow("V1.7.1")]
    [DataRow("v1.7.1.0")]
    [DataRow("v1.7.1+build")]
    [DataRow("v1.7.2")]
    [DataRow("v1.7.1-beta.1")]
    public async Task ReleaseNotes_Current_ResponseMustMatchExactRequestedTag(string responseTag)
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
        {
            calls++;
            return NotesResponse(responseTag);
        }));
        await Assert.ThrowsExactlyAsync<FormatException>(() => source.GetReleaseNotesForVersionAsync("v1.7.1"));
        Assert.AreEqual(1, calls, "非 404 的版本校验失败不得继续尝试其它标签。");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("v1")]
    [DataRow("not-a-version")]
    public async Task ReleaseNotes_Current_InvalidVersion_DoesNotRequest(string version)
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(_ =>
        {
            calls++;
            return new(HttpStatusCode.NotFound);
        }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => source.GetReleaseNotesForVersionAsync(version));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReleaseNotes_LatestAndCurrent_KeepSeparateCacheAndETags(bool sameTag)
    {
        Dictionary<string, int> calls = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            bool latest = url.Contains("/latest/download/", StringComparison.Ordinal);
            string tag = latest && !sameTag ? "v2.0.0" : "v1.7.1";
            string etag = latest ? "\"latest\"" : "\"current\"";
            int count = calls.GetValueOrDefault(url) + 1;
            calls[url] = count;
            if (count > 1)
            {
                Assert.AreEqual(etag, request.Headers.IfNoneMatch.Single().ToString());
                return new(HttpStatusCode.NotModified);
            }
            Assert.IsEmpty(request.Headers.IfNoneMatch);
            return NotesResponse(tag, tag[1..], etag);
        })) { ReleaseNotesCacheDuration = TimeSpan.Zero };

        ReleaseNotes? latest = await source.GetLatestReleaseNotesAsync();
        ReleaseNotes? current = await source.GetReleaseNotesForVersionAsync("v1.7.1");
        Assert.AreSame(current, await source.GetReleaseNotesForVersionAsync("v1.7.1"));
        Assert.AreSame(latest, await source.GetLatestReleaseNotesAsync());
        Assert.AreEqual(sameTag ? "v1.7.1" : "v2.0.0", latest!.Manifest!.Tag);
        Assert.AreEqual("v1.7.1", current!.Manifest!.Tag);
        Assert.HasCount(2, calls);
        Assert.IsTrue(calls.Values.All(count => count == 2));
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("{\"tag\":\"v2.0.0-beta.1\"}")]
    [DataRow("{\"tag\":\"v1\"}")]
    public async Task ReleaseNotes_InvalidRefresh_PreservesCachedSnapshotAndETag(string badJson)
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                return NotesResponse("v1.7.1", etag: "\"original\"");
            }
            Assert.AreEqual("\"original\"", request.Headers.IfNoneMatch.Single().ToString());
            return calls == 2
                ? new(HttpStatusCode.OK) { Content = new StringContent(badJson) }
                : new(HttpStatusCode.NotModified);
        })) { ReleaseNotesCacheDuration = TimeSpan.Zero };

        ReleaseNotes? original = await source.GetLatestReleaseNotesAsync();
        if (badJson == "invalid")
        {
            await Assert.ThrowsAsync<JsonException>(() => source.GetLatestReleaseNotesAsync());
        }
        else
        {
            await Assert.ThrowsExactlyAsync<FormatException>(() => source.GetLatestReleaseNotesAsync());
        }
        Assert.AreSame(original, await source.GetLatestReleaseNotesAsync());
        Assert.IsNotNull(original!.Manifest);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task ReleaseNotes_Cache_IsBoundedToEightUrls()
    {
        int calls = 0;
        GitHubReleaseSource source = new("https://github.com/owner/repo", new StubHandler(request =>
        {
            calls++;
            return NotesResponse(request.RequestUri!.Segments[^2].TrimEnd('/'));
        }));
        for (int i = 1; i <= 9; i++)
        {
            await source.GetReleaseNotesForVersionAsync($"v{i}.0.0");
        }
        await source.GetReleaseNotesForVersionAsync("v9.0.0");
        Assert.AreEqual(9, calls);
        await source.GetReleaseNotesForVersionAsync("v1.0.0");
        Assert.AreEqual(10, calls, "第九个 URL 应逐出最早的缓存条目。");
    }

    [TestMethod]
    public async Task ReleaseNotes_ConcurrentDifferentUrls_KeepResultsSeparateAndDeduplicateSameUrl()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> urls = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", new AsyncStubHandler(async (request, token) =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            urls.Add(url);
            if (url.Contains("/latest/download/", StringComparison.Ordinal))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return NotesResponse("v3.0.0", "3.0.0");
            }
            string tag = request.RequestUri.Segments[^2].TrimEnd('/');
            return NotesResponse(tag, tag[1..]);
        }));

        Task<ReleaseNotes?> latest = source.GetLatestReleaseNotesAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using CancellationTokenSource cancellation = new();
            Task<ReleaseNotes?> cancelled = source.GetReleaseNotesForVersionAsync("v1.0.0", cancellation.Token);
            Task<ReleaseNotes?> current = source.GetReleaseNotesForVersionAsync("v1.0.0");
            Task<ReleaseNotes?> other = source.GetReleaseNotesForVersionAsync("v2.0.0");
            Task<ReleaseNotes?> same = source.GetReleaseNotesForVersionAsync("v1.0.0");
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
            release.TrySetResult();

            ReleaseNotes?[] notes = await Task.WhenAll(latest, current, other, same).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSequenceEqual(["v3.0.0", "v1.0.0", "v2.0.0", "v1.0.0"], [.. notes.Select(note => note!.Tag)]);
            Assert.AreSame(notes[1], notes[3]);
            Assert.HasCount(3, urls);
            Assert.AreEqual(3, urls.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static HttpResponseMessage NotesResponse(string tag, string version = "1.7.1", string? etag = null)
    {
        string? rid = UpdateManifest.CurrentRid();
        Assert.IsNotNull(rid);
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                version,
                tag,
                assets = new Dictionary<string, object>
                {
                    [rid] = new { name = "pkg.zip", sha256 = new string('a', 64), size = 100 },
                    ["unsupported-rid"] = new { name = "../bad.zip", sha256 = "bad", size = -1 }
                },
                releaseNotes = new { body = "notes for " + tag }
            }))
        };
        if (etag != null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }
        return response;
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class StalledReadStream(TaskCompletionSource started) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class CancellableHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }

    private static UpdateAsset Asset(long size) => new("pkg.zip", "unused", size);

    private static HttpResponseMessage FullResponse(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class SynchronousProgress(Action<int> handler) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => handler(value.Percent);
    }

    /// <summary>收整条快照的同步进度器(百分比进度器只留 Percent,看不到字节与速率)。</summary>
    private sealed class ByteProgress(Action<UpdateDownloadProgress> handler) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => handler(value);
    }
}
