using VelaShell.Core.Models;

namespace VelaShell.Core.Credentials;

/// <summary>供选择器展示的凭据摘要,不携带密码或私钥口令。</summary>
public sealed record SharedCredentialMetadata(Guid Id, string Name, string Username, AuthMethod AuthMethod, bool HasPassword);
