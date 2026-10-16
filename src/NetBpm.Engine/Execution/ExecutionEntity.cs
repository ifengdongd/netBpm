using NetBpm.Bpmn.Model;
using NetBpm.El;

namespace NetBpm.Engine.Execution;

/// <summary>
/// 执行树节点的默认实现。
/// <para>
/// 标志位不变量（风险 R3 的断言依据）：
/// <c>IsConcurrent ⇒ ParentId != null</c>；根执行 <c>IsScope == true &amp;&amp; ParentId == null</c>。
/// </para>
/// <para>
/// 变量语义照搬 Activiti 的 <c>VariableScopeImpl</c>：懒加载本作用域全部变量、命中本作用域即改本地、
/// 否则上溯父作用域、到顶才新建。跨命令读回时靠 <see cref="ParentLoader"/> 把父执行加载回来。
/// </para>
/// </summary>
public class ExecutionEntity : IExecutionEntity
{
    private readonly Dictionary<string, VariableInstance> _variables = new(StringComparer.Ordinal);
    private IFlowElement? _currentFlowElement;
    private bool _variablesLoaded;

    /// <inheritdoc />
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    public int Revision { get; set; }

    /// <inheritdoc />
    public string? ProcessInstanceId { get; set; }

    /// <inheritdoc />
    public string? ProcessDefinitionId { get; set; }

    /// <inheritdoc />
    public string? ParentId { get; set; }

    /// <inheritdoc />
    public string? SuperExecutionId { get; set; }

    /// <inheritdoc />
    public string? BusinessKey { get; set; }

    /// <inheritdoc />
    public string? RootProcessInstanceId { get; set; }

    /// <summary>流程实例名（<c>NAME_</c>）。本子集不设置，恒为 <see langword="null"/>。</summary>
    public string? Name { get; set; }

    /// <inheritdoc />
    public string? TenantId { get; set; }

    /// <inheritdoc />
    public bool IsActive { get; set; } = true;

    /// <inheritdoc />
    public bool IsConcurrent { get; set; }

    /// <inheritdoc />
    public bool IsScope { get; set; }

    /// <inheritdoc />
    public bool IsEventScope { get; set; }

    /// <inheritdoc />
    public bool IsEnded { get; set; }

    /// <inheritdoc />
    public int SuspensionState { get; set; } = 1;

    /// <inheritdoc />
    public DateTime? StartTime { get; set; }

    /// <inheritdoc />
    public string? StartUserId { get; set; }

    /// <summary>当前活动 id（与 <see cref="CurrentFlowElement"/> 保持同步）。</summary>
    public string? CurrentActivityId { get; set; }

    /// <inheritdoc />
    public IFlowElement? CurrentFlowElement
    {
        get => _currentFlowElement;
        set
        {
            _currentFlowElement = value;
            CurrentActivityId = value?.Id;
        }
    }

    /// <inheritdoc />
    public IExecutionEntity? Parent { get; set; }

    /// <inheritdoc />
    public IList<IExecutionEntity> Children { get; } = [];

    /// <inheritdoc />
    public IVariableInstanceManager? VariableInstanceManager { get; set; }

    /// <inheritdoc />
    public IExecutionManager? ParentLoader { get; set; }

    /// <inheritdoc />
    public bool IsProcessInstanceType => ParentId is null;

    /// <inheritdoc />
    public string? EventName { get; set; }

    /// <inheritdoc />
    public IVariableScope Variables => this;

    /// <summary>当前执行持有的变量（值视图，诊断用）。</summary>
    public IReadOnlyDictionary<string, object?> VariableInstances =>
        _variables.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);

    /// <summary>当前执行持有的变量实例（诊断用）。</summary>
    public IReadOnlyList<VariableInstance> VariableInstanceList => [.. _variables.Values];

    /// <inheritdoc />
    public void Inactivate() => IsActive = false;

    /// <inheritdoc />
    public void Activate() => IsActive = true;

    // ─────────────────────────── 变量（参照 VariableScopeImpl）───────────────────────────

    /// <inheritdoc />
    public async ValueTask<object?> GetVariableAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        await EnsureVariablesLoadedAsync(ct).ConfigureAwait(false);

        if (_variables.TryGetValue(name, out var variable))
        {
            return variable.Value;
        }

        // 作用域链上溯：本作用域未命中时询问父作用域（Activiti: getVariableInstance 的父作用域分支）
        var parent = await ResolveParentAsync(ct).ConfigureAwait(false);
        return parent is null ? null : await parent.GetVariableAsync(name, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> HasVariableAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        await EnsureVariablesLoadedAsync(ct).ConfigureAwait(false);

        if (_variables.ContainsKey(name))
        {
            return true;
        }

        var parent = await ResolveParentAsync(ct).ConfigureAwait(false);
        return parent is not null && await parent.HasVariableAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 设置变量。对应 Activiti <c>VariableScopeImpl.setVariable</c>：<b>本作用域已有则改本地，
    /// 否则上溯父作用域，到顶仍无则建在本地</b> —— 即尽量把变量放到最高的作用域，
    /// 这样并行的兄弟分支才能互相看到。
    /// </summary>
    public async ValueTask SetVariableAsync(string name, object? value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        await EnsureVariablesLoadedAsync(ct).ConfigureAwait(false);

        if (_variables.ContainsKey(name))
        {
            await SetLocalVariableAsync(name, value, ct).ConfigureAwait(false);
            return;
        }

        var parent = await ResolveParentAsync(ct).ConfigureAwait(false);

        if (parent is not null)
        {
            await parent.SetVariableAsync(name, value, ct).ConfigureAwait(false);
            return;
        }

        await SetLocalVariableAsync(name, value, ct).ConfigureAwait(false);
    }

    /// <summary>只写当前执行（对应 Activiti 的 <c>setVariableLocal</c>）。</summary>
    public async ValueTask SetLocalVariableAsync(string name, object? value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        await EnsureVariablesLoadedAsync(ct).ConfigureAwait(false);

        if (_variables.TryGetValue(name, out var existing))
        {
            if (VariableInstanceManager is not null)
            {
                await VariableInstanceManager.UpdateAsync(existing, value, ct).ConfigureAwait(false);
            }
            else
            {
                // 内存宿主（无数据管理器）：变量的唯一真身就是执行对象上这个实例
                // （见 IVariableInstanceManager 的类注释）。
                // 这里必须就地改值 —— 少了这一步，第二次写同名变量会被静默丢弃，
                // 网关仍读到上一次的旧值。
                existing.Value = value;
            }

            return;
        }

        var variable = VariableInstanceManager is null
            ? new VariableInstance { Name = name, Value = value, ExecutionId = Id, ProcessInstanceId = ProcessInstanceId ?? Id }
            : VariableInstanceManager.Create(Id, ProcessInstanceId ?? Id, name, value);

        _variables[name] = variable;

        if (VariableInstanceManager is not null)
        {
            await VariableInstanceManager.InsertAsync(variable, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IDictionary<string, object?>> GetVariablesAsync(CancellationToken ct = default)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);

        // 先父后子：子作用域覆盖父作用域（Activiti collectVariables 的顺序）
        var parent = await ResolveParentAsync(ct).ConfigureAwait(false);

        if (parent is not null)
        {
            foreach (var (key, value) in await parent.GetVariablesAsync(ct).ConfigureAwait(false))
            {
                merged[key] = value;
            }
        }

        await EnsureVariablesLoadedAsync(ct).ConfigureAwait(false);

        foreach (var (key, variable) in _variables)
        {
            merged[key] = variable.Value;
        }

        return merged;
    }

    /// <inheritdoc />
    public async ValueTask SetVariablesAsync(IDictionary<string, object?> variables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variables);

        // 逐个走 SetVariableAsync：保持与 Activiti 相同的作用域落点
        foreach (var (key, value) in variables)
        {
            await SetVariableAsync(key, value, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveVariableAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        await EnsureVariablesLoadedAsync(ct).ConfigureAwait(false);

        if (_variables.Remove(name, out var removed))
        {
            if (VariableInstanceManager is not null)
            {
                // 历史账本「只增不删」（与 ACT_HI_IDENTITYLINK 同策略）：只删运行行 + 字节行
                await VariableInstanceManager.DeleteAsync(removed, ct).ConfigureAwait(false);
            }

            return true;
        }

        // 作用域链上溯：变量可能在更高的作用域上
        var parent = await ResolveParentAsync(ct).ConfigureAwait(false);
        return parent is not null && await parent.RemoveVariableAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 懒加载本作用域变量。对应 <c>VariableScopeImpl.ensureVariableInstancesInitialized()</c>：
    /// 第一次访问时一次性把该作用域的全部变量读出来（<c>selectVariablesByExecutionId</c>）。
    /// </summary>
    private async ValueTask EnsureVariablesLoadedAsync(CancellationToken ct)
    {
        if (_variablesLoaded)
        {
            return;
        }

        _variablesLoaded = true;

        // 尚未落库的执行没有变量行；内存宿主没有端口
        if (VariableInstanceManager is null || string.IsNullOrEmpty(Id))
        {
            return;
        }

        foreach (var variable in await VariableInstanceManager.FindByExecutionIdAsync(Id, ct).ConfigureAwait(false))
        {
            _variables[variable.Name] = variable;
        }
    }

    /// <summary>
    /// 取父作用域。对应 Activiti <c>ExecutionEntityImpl.getParent()</c> 的懒加载：
    /// 跨命令从库读回的实体只有 <c>PARENT_ID_</c>，需要时再把父执行查回来。
    /// </summary>
    private async ValueTask<IExecutionEntity?> ResolveParentAsync(CancellationToken ct)
    {
        if (Parent is not null || ParentId is null || ParentLoader is null)
        {
            return Parent;
        }

        Parent = await ParentLoader.FindByIdAsync(ParentId, ct).ConfigureAwait(false);
        return Parent;
    }

    public override string ToString() =>
        $"{Id} (activityId={CurrentActivityId}, active={IsActive}, concurrent={IsConcurrent}, scope={IsScope})";
}
