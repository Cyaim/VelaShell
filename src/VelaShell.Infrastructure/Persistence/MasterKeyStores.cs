using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>按平台挑出本机加密密钥用的系统密钥链。</summary>
public static class MasterKeyStores
{
    /// <summary>
    /// 设为 <c>1</c> 时不用系统密钥链,本机加密密钥照旧留在数据目录的密钥文件里。
    /// </summary>
    /// <remarks>
    /// 一是排障退路:某台机器上的 keyring 出了怪事(每次启动都弹解锁框、D-Bus 卡住),用户不必等新版就能绕开;
    /// 已经迁进密钥链的密钥不会因此丢 —— 清单还在,去掉这个变量的那次启动照常取回。
    /// 二是测试工程的安全网(<c>tests/velashell.runsettings</c> 设了它):测试不该往开发机的钥匙串里写条目。
    /// </remarks>
    public const string DisableEnvironmentVariable = "VELASHELL_NO_KEYCHAIN";

    /// <summary>
    /// 这个平台的系统密钥链:macOS 钥匙串、Linux 的 Secret Service;Windows 与其它平台为 null
    /// (Windows 上本地密钥文件本来就以 DPAPI 包裹)。被 <see cref="DisableEnvironmentVariable" /> 关掉时
    /// 交回一个永远「用不了」的占位,好让状态里如实写着原因。
    /// </summary>
    /// <param name="dataRoot">数据根(写进 Secret Service 条目的标签,同一用户开了几个 <c>--data-root</c> 时分得清)。</param>
    public static IMasterKeyStore? ForCurrentPlatform(string dataRoot)
    {
        IMasterKeyStore? store = OperatingSystem.IsMacOS() ? new MacKeychainMasterKeyStore()
            : OperatingSystem.IsLinux() ? new SecretServiceMasterKeyStore(dataRoot)
            : null;
        if (store is not null && Environment.GetEnvironmentVariable(DisableEnvironmentVariable) == "1")
        {
            return new DisabledMasterKeyStore(store.Name, $"{DisableEnvironmentVariable}=1");
        }
        return store;
    }

    /// <summary>被关掉的密钥链:什么都不做,一律「用不了」。</summary>
    private sealed class DisabledMasterKeyStore(string name, string reason) : IMasterKeyStore
    {
        public string Name { get; } = name;

        public MasterKeyStoreResult Load(string keyId) => MasterKeyStoreResult.Unavailable(reason);

        public MasterKeyStoreResult Save(string keyId, ReadOnlySpan<byte> key) => MasterKeyStoreResult.Unavailable(reason);
    }
}
