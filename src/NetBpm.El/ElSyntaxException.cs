namespace NetBpm.El;

/// <summary>
/// 词法 / 语法错误。对照 JUEL 的 <c>ScanException</c> / <c>ParseException</c>
/// （两者在 JUEL 里都被 <c>TreeBuilderException</c> 包装后抛出）。
/// <para>它是<b>响亮失败</b>：解析期就报错，绝不"先跑起来再说"。</para>
/// </summary>
public sealed class ElSyntaxException : ElException
{
    public ElSyntaxException(string message)
        : base(message)
    {
    }

    public ElSyntaxException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
