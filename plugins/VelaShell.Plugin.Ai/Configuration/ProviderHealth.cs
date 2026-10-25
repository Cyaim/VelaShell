namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 模型接入与 Key 槽的被动健康记录:失败进冷却,成功即解除。
/// </summary>
/// <remarks>
/// <para>
/// 这里记的是"刚才失败过"这个事实,给故障转移挑下一站时当过滤用 ——
/// 一个刚挂的端点不值得再拿用户这一轮去撞。它<b>不拦用户主动选择</b>:
/// 冷却中的接入照样能从下拉里选、能发消息,只是自动切换先绕开它。
/// </para>
/// <para>
/// 只活在内存里:重启即清。它描述的是"此刻",跨会话记住一次陈年失败
/// 只会让一个早已恢复的端点永远排不上号。
/// </para>
/// <para>
/// Key 槽有两种冷却:<b>整把</b>(<see cref="RecordKey" />,401 —— 这把 Key 本身被拒,换哪个模型都一样)
/// 与<b>只对一个模型</b>(<see cref="RecordKeyForModel" />,403 / 429 —— 可能只是这把 Key 用不了这一个型号、
/// 或这个型号单独限流)。后者不连坐同一把 Key 下的兄弟模型:故障转移挑到它们时照常探活。
/// </para>
/// </remarks>
public sealed class ProviderHealth
{
    /// <summary>
    /// 失败后的冷却时长。一分钟足够跨过一次抖动,又不至于让一个刚恢复的接入
    /// 在整场对话里都轮不上 —— 下一次谁再用它成功了,冷却就地解除。
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    /// <summary>一条健康记录记在谁头上:模型、整把 Key 槽,或「某把 Key 用在某个模型上」。</summary>
    private enum Kind : byte
    {
        Model,
        Key,
        KeyForModel
    }

    /// <summary>记录的键;<see cref="Scope" /> 只对 <see cref="Kind.KeyForModel" /> 有值(模型 id)。三种互不共用键空间。</summary>
    private readonly record struct Subject(Kind Kind, string Id, string Scope = "");

    /// <summary>失败时刻(<see cref="Environment.TickCount64" />,单调毫秒);没记过就没有这一项。</summary>
    /// <remarks>
    /// 用单调时钟而不是墙上时间:墙钟会被 NTP / 手动回拨,回拨多少秒,冷却就白拖多少秒 ——
    /// "刚才失败过"这个事实不因时钟怎么拨而改变。<see cref="Environment.TickCount64" />
    /// 只会随开机时间前进,回拨免疫。
    /// </remarks>
    private readonly Dictionary<Subject, long> _failedAt = [];

    /// <summary>每条记录接受的证据序号,成功也保留以挡住迟到旧失败。</summary>
    private readonly Dictionary<Subject, long> _latestObservation = [];
    private long _observationId;
    private readonly Lock _gate = new();

    /// <summary>
    /// 证据代数:每次 <see cref="Clear" />(配置保存、接入增删)前进一代。
    /// </summary>
    private int _version;

    /// <summary>
    /// 当前证据代数 —— 发请求之前记下它,收尾回写时一并交给 <see cref="Record" />。
    /// </summary>
    /// <remarks>
    /// 配置一改,旧证据描述的就是<b>另一份</b>配置:地址 / Key 修正之后,在途的旧请求
    /// 仍可能按同一个模型 id 稍晚失败 —— 带着旧代数回来的写入会被直接丢弃,
    /// 否则刚修好的接入立刻被重新冷却(review⑥)。代数只增不回绕到可用范围内。
    /// </remarks>
    public int Version => Volatile.Read(ref _version);

    /// <summary>请求发出前取序号;不带序号的即时结果在记账时取得新序号。</summary>
    public long BeginObservation()
    {
        lock (_gate)
        {
            return ++_observationId;
        }
    }

    /// <summary>记一次结果:成功解除冷却,失败开始计时。</summary>
    /// <param name="modelId">模型配置 ID;空值直接忽略。</param>
    /// <param name="ok">这次是成(解除)还是败(开始冷却)。</param>
    /// <param name="evidenceVersion">
    /// 这份结果所属的证据代数(<see cref="Version" />,请求发出时记下);与当前代对不上
    /// 就丢弃 —— 那是改配置<b>之前</b>发出去的请求,它的成败描述的是旧配置。
    /// null 表示不校验(纯内存即时结果,或调用方明确不在乎)。
    /// </param>
    /// <param name="observationId">请求起跑时取的序号;null 表示结果完成时才排序。</param>
    public void Record(string? modelId, bool ok, int? evidenceVersion = null, long? observationId = null)
    {
        if (string.IsNullOrEmpty(modelId)) return;
        RecordCore([new Subject(Kind.Model, modelId)], ok, evidenceVersion, observationId);
    }

    /// <summary>记录整把 Key 槽的证据(401:这把 Key 本身被拒);与任何模型 ID 都不共用键空间。</summary>
    public void RecordKey(string? keyId, bool ok, int? evidenceVersion = null, long? observationId = null)
    {
        if (string.IsNullOrEmpty(keyId)) return;
        RecordCore([new Subject(Kind.Key, keyId)], ok, evidenceVersion, observationId);
    }

    /// <summary>
    /// 记录某把 Key 用在某个模型上的结果。失败只冷这一对(403 / 429:可能只是这个型号没开通或单独限流),
    /// 同一把 Key 下的兄弟模型照常可用;成功说明这把 Key 能用,整把的冷却与这一对的冷却一起解除。
    /// </summary>
    public void RecordKeyForModel(string? keyId, string? modelId, bool ok, int? evidenceVersion = null, long? observationId = null)
    {
        if (string.IsNullOrEmpty(keyId)) return;
        if (string.IsNullOrEmpty(modelId))
        {
            RecordKey(keyId, ok, evidenceVersion, observationId);
            return;
        }
        Subject pair = new(Kind.KeyForModel, keyId, modelId);
        RecordCore(ok ? [pair, new Subject(Kind.Key, keyId)] : [pair], ok, evidenceVersion, observationId);
    }

    /// <summary>
    /// 记一次 Key 槽的鉴权 / 额度失败:<paramref name="wholeKey" />(401,Key 本身被拒)冷整把 Key,
    /// 否则(403 / 429)只冷这把 Key 用在 <paramref name="modelId" /> 上。
    /// </summary>
    public void RecordKeyFailure(string? keyId, string? modelId, bool wholeKey, int? evidenceVersion = null, long? observationId = null)
    {
        if (wholeKey) RecordKey(keyId, false, evidenceVersion, observationId);
        else RecordKeyForModel(keyId, modelId, false, evidenceVersion, observationId);
    }

    private void RecordCore(Subject[] subjects, bool ok, int? evidenceVersion, long? observationId)
    {
        lock (_gate)
        {
            if (evidenceVersion is { } version && version != _version)
            {
                return; // 陈旧证据:配置已经换过一轮,这次成败不该落在新配置头上
            }
            long observed = observationId ?? ++_observationId;
            foreach (Subject subject in subjects)
            {
                if (_latestObservation.TryGetValue(subject, out long latest) && observed < latest)
                {
                    continue; // 同一记录已有更新的证据,旧请求迟到也不能覆盖
                }
                _latestObservation[subject] = observed;
                if (ok)
                {
                    _failedAt.Remove(subject);
                }
                else
                {
                    _failedAt[subject] = Environment.TickCount64;
                }
            }
        }
    }

    /// <summary>指定模型是否仍在冷却中。</summary>
    public bool IsCooling(string? modelId)
        => !string.IsNullOrEmpty(modelId) && IsCoolingCore(new Subject(Kind.Model, modelId));

    /// <summary>指定 Key 槽是否整把仍在冷却中(设置页 Key 行显示的就是这个)。</summary>
    public bool IsKeyCooling(string? keyId)
        => !string.IsNullOrEmpty(keyId) && IsCoolingCore(new Subject(Kind.Key, keyId));

    /// <summary>这把 Key 用在这个模型上是否仍在冷却中:整把在冷,或只对这个模型冷着。</summary>
    public bool IsKeyCooling(string? keyId, string? modelId)
        => IsKeyCooling(keyId)
            || !string.IsNullOrEmpty(keyId) && !string.IsNullOrEmpty(modelId)
            && IsCoolingCore(new Subject(Kind.KeyForModel, keyId, modelId));

    private bool IsCoolingCore(Subject subject)
    {
        lock (_gate)
        {
            return _failedAt.TryGetValue(subject, out long at)
                   && Environment.TickCount64 - at < (long)Cooldown.TotalMilliseconds;
        }
    }

    /// <summary>仅使一个模型的旧配置证据失效(连同各把 Key 用在它上面的记录),保留其他记录及全局代数。</summary>
    public void Invalidate(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return;
        InvalidateCore(subject => subject.Kind == Kind.Model && subject.Id == modelId
            || subject.Kind == Kind.KeyForModel && subject.Scope == modelId, new Subject(Kind.Model, modelId));
    }

    /// <summary>仅使一个 Key 槽的旧配置证据失效(整把与用在各模型上的记录)。</summary>
    public void InvalidateKey(string? keyId)
    {
        if (string.IsNullOrEmpty(keyId)) return;
        InvalidateCore(subject => subject.Kind is Kind.Key or Kind.KeyForModel && subject.Id == keyId,
            new Subject(Kind.Key, keyId));
    }

    private void InvalidateCore(Func<Subject, bool> matches, Subject primary)
    {
        lock (_gate)
        {
            long observed = ++_observationId;
            // 已有记录的一律挡住迟到的旧结果;主键哪怕还没记过也挡上(与原先一样)
            foreach (Subject subject in _latestObservation.Keys.Where(matches).Append(primary).Distinct().ToArray())
            {
                _failedAt.Remove(subject);
                _latestObservation[subject] = observed;
            }
            foreach (Subject subject in _failedAt.Keys.Where(matches).ToArray()) _failedAt.Remove(subject);
        }
    }

    /// <summary>
    /// 全部解除。供应商 / 模型配置改了之后调用 —— 旧证据描述的是<b>另一份</b>配置:
    /// 地址、Key、协议都可能换了,记录只按模型或 Key 槽 ID 记,换完配置后仍是同一个 ID,
    /// 留着冷却只会让修好的接入在接下来 60 秒里继续被自动切换绕开。
    /// 放心清:挑下一站时还有切换前探测兜底,真坏的会被再探一次拦住。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _failedAt.Clear();
            _latestObservation.Clear();
            // 代数跟着前进:此刻还没回来的在途请求,回写时会对不上号被丢弃(见 Version)
            Interlocked.Increment(ref _version);
        }
    }
}
