namespace NetBpm.Engine;

/// <summary>无返回值命令的占位类型（等价于 <c>void</c> 的命令式表达）。</summary>
public readonly struct Unit : IEquatable<Unit>
{
    /// <summary>唯一实例。</summary>
    public static readonly Unit Value;

    public bool Equals(Unit other) => true;

    public override bool Equals(object? obj) => obj is Unit;

    public override int GetHashCode() => 0;

    public override string ToString() => "()";

    public static bool operator ==(Unit left, Unit right) => true;

    public static bool operator !=(Unit left, Unit right) => false;
}
