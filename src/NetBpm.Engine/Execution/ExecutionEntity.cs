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
            ? new VariableInstance
            {
                Name = name,
                Value = value,
                ExecutionId = Id,
                ProcessInstanceId = ProcessInstanceId ?? Id,
                TenantId = TenantId,
            }
            : VariableInstanceManager.Create(Id, ProcessInstanceId ?? Id, name, value, TenantId);

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
    /// 沿作用域链<b>批量</b>预取变量，一次往返填满链上所有作用域的缓存（N+1 消除）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要它：流程变量落在<b>流程实例执行</b>上，而网关判定常发生在<b>子执行</b>上。
    /// 原来的 <see cref="EnsureVariablesLoadedAsync"/> 是「每个作用域一次查询」，
    /// 作用域链越长、网关越多，SQL 往返越多 —— 典型 N+1。
    /// </para>
    /// <para>
    /// 本方法分两步：① 沿 <c>PARENT_ID_</c> 上溯收集整条链的执行实体；
    /// ② 用 <see cref="IVariableInstanceManager.FindByExecutionIdsAsync"/> 一次取回全部变量行，
    /// 再按 <c>EXECUTION_ID_</c> 回填到各作用域并标记为已加载。
    /// 之后链上任何一次 <c>GetVariableAsync</c> 都是纯内存命中。
    /// </para>
    /// <para>
    /// 语义安全：预取只做「提前填充」，不改变求值口径 ——
    /// 未命中预取的变量仍会在求值时按原有作用域链上溯（不会因预取而查不到）。
    /// 内存宿主（无端口）或已加载时直接短路。
    /// </para>
    /// </remarks>
    /// <param name="names">需要预取的变量名；为空时不做任何查询。</param>
    /// <param name="ct">取消标记。</param>
    public async ValueTask PrefetchVariablesAsync(
        IReadOnlyCollection<string> names,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (names.Count == 0 || VariableInstanceManager is null)
        {
            return;
        }

        var chain = await CollectScopeChainAsync(ct).ConfigureAwait(false);

        // 需要的名字已在本作用域命中 ⇒ 不必上溯（流程实例执行时是最常见的快路径）
        if (chain[0]._variablesLoaded && names.All(name => chain[0]._variables.ContainsKey(name)))
        {
            return;
        }

        var pending = chain
            .Where(execution => !execution._variablesLoaded)
            .Select(execution => execution.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var rows = await VariableInstanceManager
            .FindByExecutionIdsAsync(pending, ct)
            .ConfigureAwait(false);

        var byExecution = rows
            .GroupBy(row => row.ExecutionId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var execution in chain)
        {
            // 只把调用方点名的变量预热进缓存：其余变量等真正访问时再按原路径加载。
            // 这样「预取」不会把大对象 / 超长文本无谓地拖进内存。
            if (byExecution.TryGetValue(execution.Id, out var variables))
            {
                foreach (var variable in variables)
                {
                    // 已存在的条目是本命令内刚写过的「真身」，保留它，避免被库里的旧值覆盖
                    if (!execution._variables.ContainsKey(variable.Name))
                    {
                        execution._variables[variable.Name] = variable;
                    }
                }
            }

            execution._variablesLoaded = true;
        }
    }

    /// <summary>
    /// 收集从本执行一路到根的完整作用域链（本执行在最前）。
    /// </summary>
    private async ValueTask<List<ExecutionEntity>> CollectScopeChainAsync(CancellationToken ct)
    {
        var chain = new List<ExecutionEntity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = this;

        // 执行树深度有限，这里的上限只是防御性的（避免脏数据 / 自引用造成死循环）
        for (var depth = 0; current is not null && depth < 64; depth++)
        {
            if (!seen.Add(current.Id))
            {
                break;
            }

            chain.Add(current);

            if (current.ParentId is null || ParentLoader is null)
            {
                break;
            }

            current = current.Parent as ExecutionEntity
                ?? await ParentLoader.FindByIdAsync(current.ParentId, ct) as ExecutionEntity;
        }

        return chain;
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

    /// <summary>
    /// 捕获可变状态（内存宿主的命令级回滚材料）。
    /// </summary>
    /// <remarks>
    /// 只抓「命令会改的东西」：标量字段、父子结构、变量字典。缓存的对象引用（如
    /// <see cref="ParentLoader"/>）不参与 —— 它们不随命令变化。
    /// </remarks>
    internal ExecutionEntitySnapshot CaptureState() => new(
        Revision,
        _currentFlowElement,
        IsActive,
        IsConcurrent,
        IsScope,
        IsEventScope,
        IsEnded,
        SuspensionState,
        StartTime,
        StartUserId,
        Name,
        BusinessKey,
        TenantId,
        ProcessInstanceId,
        ProcessDefinitionId,
        ParentId,
        Parent,
        [.. Children],
        _variables.ToDictionary(
            pair => pair.Key,
            pair => (pair.Value, pair.Value.Value),
            StringComparer.Ordinal),
        _variablesLoaded);

    /// <summary>把状态恢复成快照（命令失败时由内存宿主调用）。</summary>
    internal void RestoreState(ExecutionEntitySnapshot snapshot)
    {
        Revision = snapshot.Revision;
        CurrentFlowElement = snapshot.CurrentFlowElement;
        IsActive = snapshot.IsActive;
        IsConcurrent = snapshot.IsConcurrent;
        IsScope = snapshot.IsScope;
        IsEventScope = snapshot.IsEventScope;
        IsEnded = snapshot.IsEnded;
        SuspensionState = snapshot.SuspensionState;
        StartTime = snapshot.StartTime;
        StartUserId = snapshot.StartUserId;
        Name = snapshot.Name;
        BusinessKey = snapshot.BusinessKey;
        TenantId = snapshot.TenantId;
        ProcessInstanceId = snapshot.ProcessInstanceId;
        ProcessDefinitionId = snapshot.ProcessDefinitionId;
        ParentId = snapshot.ParentId;
        Parent = snapshot.Parent;

        Children.Clear();

        foreach (var child in snapshot.Children)
        {
            Children.Add(child);
        }

        _variables.Clear();

        foreach (var (name, entry) in snapshot.Variables)
        {
            // 变量实例对象本身是「真身」（内存宿主没有变量管理器），值必须写回同一实例
            entry.Instance.Value = entry.Value;
            _variables[name] = entry.Instance;
        }

        _variablesLoaded = snapshot.VariablesLoaded;
    }
}

/// <summary>
/// <see cref="ExecutionEntity"/> 的可变状态快照（内存宿主命令级回滚用）。
/// </summary>
internal sealed record ExecutionEntitySnapshot(
    int Revision,
    IFlowElement? CurrentFlowElement,
    bool IsActive,
    bool IsConcurrent,
    bool IsScope,
    bool IsEventScope,
    bool IsEnded,
    int SuspensionState,
    DateTime? StartTime,
    string? StartUserId,
    string? Name,
    string? BusinessKey,
    string? TenantId,
    string? ProcessInstanceId,
    string? ProcessDefinitionId,
    string? ParentId,
    IExecutionEntity? Parent,
    IExecutionEntity[] Children,
    Dictionary<string, (VariableInstance Instance, object? Value)> Variables,
    bool VariablesLoaded);
