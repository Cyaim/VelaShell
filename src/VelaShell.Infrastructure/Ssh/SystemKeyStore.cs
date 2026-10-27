using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// Windows CNG 的系统密钥库(<see cref="ISystemKeyStore" />):枚举软件密钥库、TPM、智能卡里的钥,
/// 用 SSH 库的 <see cref="CngSshSigner" /> 打开每一把读出公钥;证书存储里有证书用这把钥时带上证书主题。
/// </summary>
/// <remarks>
/// <para>
/// .NET 没有枚举 CNG 钥的接口,经 <c>ncrypt.dll</c> 的 <c>NCryptEnumKeys</c> 列名字(用户级与机器级各一遍,一律带
/// <c>NCRYPT_SILENT_FLAG</c>,智能卡的密钥库没插卡时也不弹「请插入智能卡」)。打开每一把同样静默:要弹框才能打开的、
/// 没权限的(多数机器级的钥)、不是 RSA / ECDSA 的(ECDH、DH)都不列。
/// </para>
/// <para>
/// 证书主题经 <c>CertGetCertificateContextProperty(CERT_KEY_PROV_INFO_PROP_ID)</c> 读证书记着的「密钥库 + 钥名」,
/// 不去取证书的私钥 —— 取私钥会让智能卡证书在卡没插时弹框。旧式 CSP(非 CNG)里的证书私钥这里列不出来,库也打不开。
/// </para>
/// </remarks>
public sealed partial class SystemKeyStore : ISystemKeyStore
{
    /// <summary>枚举的密钥库:软件、TPM、智能卡。</summary>
    private static readonly string[] Providers =
        [SystemKeyReference.SoftwareProvider, SystemKeyReference.PlatformProvider, SystemKeyReference.SmartCardProvider];

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public Task<IReadOnlyList<SystemKeyInfo>> ListKeysAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<SystemKeyInfo>>(
            () => OperatingSystem.IsWindows() ? ListOnWindows(cancellationToken) : [],
            cancellationToken);

    [SupportedOSPlatform("windows")]
    private static List<SystemKeyInfo> ListOnWindows(CancellationToken cancellationToken)
    {
        Dictionary<(string Provider, string Name, bool Machine), string> subjects = CertificateSubjects();
        List<SystemKeyInfo> keys = [];
        foreach (string provider in Providers)
        {
            foreach (bool machine in (bool[])[false, true])
            {
                foreach (string name in KeyNames(provider, machine))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Describe(provider, name, machine, subjects) is { } key)
                    {
                        keys.Add(key);
                    }
                }
            }
        }
        return keys;
    }

    /// <summary>打开一把钥读出公钥;打不开、不能用来签 SSH 的返回 <see langword="null" />。</summary>
    [SupportedOSPlatform("windows")]
    internal static SystemKeyInfo? Describe(
        string provider, string name, bool machine, IReadOnlyDictionary<(string Provider, string Name, bool Machine), string>? subjects = null)
    {
        CngKeyOpenOptions options = CngKeyOpenOptions.Silent | (machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None);
        try
        {
            using var signer = CngSshSigner.Open(name, new CngProvider(provider), options);
            string? subject = subjects?.GetValueOrDefault((provider, name, machine));
            return new SystemKeyInfo(
                new SystemKeyReference
                {
                    Provider = provider,
                    KeyName = name,
                    MachineKey = machine,
                    PublicKey = signer.PublicKey.ToOpenSshFormat(subject ?? name),
                },
                signer.PublicKey.KeyType,
                signer.PublicKey.KeyBits,
                signer.PublicKey.Sha256Fingerprint,
                subject);
        }
        catch (SshPrivateKeyException)
        {
            return null;   // 打不开(没权限、要弹框)或者不是 RSA / ECDSA:不列。
        }
    }

    // ------------------------------------------------------------ NCrypt

    private const uint NcryptMachineKeyFlag = 0x20;
    private const uint NcryptSilentFlag = 0x40;

    /// <summary>一个密钥库里的钥名;密钥库不在(没有 TPM、没有读卡器)或列不出来时为空。</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> KeyNames(string provider, bool machine)
    {
        List<string> names = [];
        if (NCryptOpenStorageProvider(out nint handle, provider, 0) != 0)
        {
            return names;
        }
        nint state = 0;
        try
        {
            uint flags = NcryptSilentFlag | (machine ? NcryptMachineKeyFlag : 0);
            while (true)
            {
                int status = NCryptEnumKeys(handle, null, out nint item, ref state, flags);
                if (status != 0)
                {
                    // 正常结束是 NTE_NO_MORE_ITEMS(0x8009002A);别的错(智能卡没插、要交互)同样到此为止,列出已有的。
                    break;
                }
                try
                {
                    nint namePointer = Marshal.ReadIntPtr(item);   // NCryptKeyName.pszName 是第一个字段
                    if (Marshal.PtrToStringUni(namePointer) is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }
                finally
                {
                    _ = NCryptFreeBuffer(item);
                }
            }
        }
        finally
        {
            if (state != 0)
            {
                _ = NCryptFreeBuffer(state);
            }
            _ = NCryptFreeObject(handle);
        }
        return names;
    }

    [LibraryImport("ncrypt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NCryptOpenStorageProvider(out nint provider, string providerName, uint flags);

    [LibraryImport("ncrypt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NCryptEnumKeys(nint provider, string? scope, out nint keyName, ref nint enumState, uint flags);

    [LibraryImport("ncrypt.dll")]
    private static partial int NCryptFreeBuffer(nint buffer);

    [LibraryImport("ncrypt.dll")]
    private static partial int NCryptFreeObject(nint handle);

    // ------------------------------------------------------------ 证书存储

    private const uint CertKeyProvInfoPropId = 2;
    private const uint CryptMachineKeyset = 0x20;

    /// <summary>
    /// 当前用户「个人」证书存储里,有私钥、私钥在 CNG 密钥库里的证书:(密钥库, 钥名, 机器级) → 证书主题。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Dictionary<(string Provider, string Name, bool Machine), string> CertificateSubjects()
    {
        Dictionary<(string, string, bool), string> subjects = [];
        try
        {
            using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (X509Certificate2 certificate in store.Certificates)
            {
                using (certificate)
                {
                    if (certificate.HasPrivateKey && KeyProviderInfo(certificate) is { } info)
                    {
                        subjects.TryAdd(info, certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) is { Length: > 0 } simple
                            ? simple
                            : certificate.Subject);
                    }
                }
            }
        }
        catch (CryptographicException)
        {
            // 证书存储打不开:钥照样列,只是没有主题。
        }
        return subjects;
    }

    /// <summary>证书记着的私钥位置(<c>CRYPT_KEY_PROV_INFO</c>);不在 CNG 里(旧式 CSP,<c>dwProvType</c> 非 0)时为 <see langword="null" />。</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe (string Provider, string Name, bool Machine)? KeyProviderInfo(X509Certificate2 certificate)
    {
        uint size = 0;
        if (!CertGetCertificateContextProperty(certificate.Handle, CertKeyProvInfoPropId, null, ref size) || size == 0)
        {
            return null;
        }
        byte[] buffer = new byte[size];
        fixed (byte* data = buffer)
        {
            if (!CertGetCertificateContextProperty(certificate.Handle, CertKeyProvInfoPropId, data, ref size))
            {
                return null;
            }
            var info = (CryptKeyProvInfo*)data;
            string? container = Marshal.PtrToStringUni(info->ContainerName);
            string? provider = Marshal.PtrToStringUni(info->ProviderName);
            return info->ProviderType == 0 && container is { Length: > 0 } && provider is { Length: > 0 }
                ? (provider, container, (info->Flags & CryptMachineKeyset) != 0)
                : null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptKeyProvInfo
    {
        public nint ContainerName;
        public nint ProviderName;
        public uint ProviderType;
        public uint Flags;
        public uint ProviderParamCount;
        public nint ProviderParams;
        public uint KeySpec;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CertGetCertificateContextProperty(nint certificate, uint propertyId, byte* data, ref uint size);
}
