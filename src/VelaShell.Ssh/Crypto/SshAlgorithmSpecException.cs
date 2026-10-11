// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  Ciphers / KexAlgorithms / MACs / HostKeyAlgorithms 的 + - ^ 写法(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7.2

namespace VelaShell.Ssh.Crypto;

/// <summary>一条算法清单写法哪里不成立。</summary>
public enum SshAlgorithmSpecProblem
{
    /// <summary>没写任何名字（只有前缀，或者只有分隔符）。</summary>
    Empty,

    /// <summary>不认识的名字（拼错了，或者根本不是这一类的）。</summary>
    Unknown,

    /// <summary>认得、但本库没实现（CBC、3des、group1……写进去也谈不成）。</summary>
    Unimplemented,

    /// <summary>删完一个不剩。</summary>
    NothingLeft,
}

/// <summary>算法清单写法不成立（<see cref="SshAlgorithmSpec.Apply"/>）。</summary>
/// <remarks>创建一个异常。</remarks>
public sealed class SshAlgorithmSpecException(SshAlgorithmCategory category, SshAlgorithmSpecProblem problem, string? name) : ArgumentException(problem switch
{
    SshAlgorithmSpecProblem.Empty => $"{category} 的清单里没有任何名字。",
    SshAlgorithmSpecProblem.Unimplemented => $"{category} 里的 {name} 本库没有实现（写进去也谈不成）。",
    SshAlgorithmSpecProblem.NothingLeft => $"{category} 的清单删完一个不剩。",
    _ => $"{category} 里不认识 {name}。",
})
{

    /// <summary>哪一类。</summary>
    public SshAlgorithmCategory Category { get; } = category;

    /// <summary>哪里不成立。</summary>
    public SshAlgorithmSpecProblem Problem { get; } = problem;

    /// <summary>出问题的那个名字（<see cref="SshAlgorithmSpecProblem.Unknown"/> / <see cref="SshAlgorithmSpecProblem.Unimplemented"/> 时）。</summary>
    public string? Name { get; } = name;
}
