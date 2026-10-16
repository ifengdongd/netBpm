namespace NetBpm.El;

/// <summary>
/// 基于字典的变量作用域，用于测试与「非持久化」场景。
/// </summary>
public class SimpleVariableScope : IVariableScope
{
    private readonly Dictionary<string, object?> _variables;

    public SimpleVariableScope()
        : this(new Dictionary<string, object?>(StringComparer.Ordinal))
    {
    }

    public SimpleVariableScope(IDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        _variables = new Dictionary<string, object?>(variables, StringComparer.Ordinal);
    }

    public ValueTask<object?> GetVariableAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ValueTask.FromResult(_variables.GetValueOrDefault(name));
    }

    public ValueTask<bool> HasVariableAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ValueTask.FromResult(_variables.ContainsKey(name));
    }

    public ValueTask SetVariableAsync(string name, object? value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        _variables[name] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IDictionary<string, object?>> GetVariablesAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IDictionary<string, object?>>(
            new Dictionary<string, object?>(_variables, StringComparer.Ordinal));

    public ValueTask SetVariablesAsync(IDictionary<string, object?> variables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variables);

        foreach (var (key, value) in variables)
        {
            _variables[key] = value;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RemoveVariableAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ValueTask.FromResult(_variables.Remove(name));
    }
}
