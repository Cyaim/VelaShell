namespace VelaShell.Presentation.Services;

/// <summary>连接测试的结果:是否成功,以及失败时的错误信息。</summary>
/// <param name="Success">连接测试是否成功。</param>
/// <param name="ErrorMessage">测试失败时的错误描述;成功时为 <see langword="null" />。</param>
/// <param name="Error">
/// 失败时的原始异常;成功(或插件探针没有异常可给)时为 <see langword="null" />。
/// 文案只够人看,宿主要从异常链里认出「算法谈不成」才给得出一键放开(<c>SshAlgorithmRemedy</c>)。
/// </param>
public sealed record ConnectionTestResult(bool Success, string? ErrorMessage = null, Exception? Error = null);
