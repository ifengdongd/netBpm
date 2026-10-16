using System.Collections.Concurrent;
using System.Dynamic;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
// 本程序集存在命名空间 NetBpm.Engine.Script，与 Roslyn 的 Script / ScriptState 类型名冲突，用别名消歧
using RoslynScript = Microsoft.CodeAnalysis.Scripting.Script;
using RoslynScriptState = Microsoft.CodeAnalysis.Scripting.ScriptState;

namespace NetBpm.Engine.Script;

/// <summary>
/// 脚本任务的<b>全局对象</b>（Roslyn globals）。脚本顶层可直接引用：
/// <list type="bullet">
///   <item>
///     <description><c>execution</c> —— <see cref="IDelegateExecution"/>（完整引擎 API，
///     含 <c>GetVariableAsync</c> / <c>SetVariableAsync</c> / 流程实例信息）。</description>
///   </item>
///   <item>
///     <description><c>vars</c> —— 进入脚本时的<b>流程变量快照</b>（dynamic，读成员即可取值）。
///     ⚠️ 它是快照：对 <c>vars.xxx</c> 赋值只改字典本身，<b>不会</b>写回流程变量；
///     持久化写回走 <c>execution.SetVariableAsync</c> 或脚本顶层变量 + <c>autoStoreVariables</c>。</description>
///   </item>
/// </list>
/// <para>
/// 变量读写语义与 Activiti 对齐（<c>ScriptTaskActivityBehavior</c> 的 JS 语义）：
/// 脚本里声明的顶层变量默认<b>不</b>写回流程；仅当 <c>activiti:autoStoreVariables="true"</c>
/// 时才全部存回；<c>activiti:resultVariable</c> 存脚本最后一条顶层表达式的值。
/// </para>
/// </summary>
public sealed class ScriptTaskGlobals
{
    /// <summary>当前执行（对应 Activiti JS 脚本里的 <c>execution</c> 隐式对象）。</summary>
    public IDelegateExecution execution { get; set; } = null!;

    /// <summary>流程变量快照（只读语义，见类型说明）。</summary>
    public dynamic vars { get; set; } = new ExpandoObject();
}

/// <summary>
/// 脚本任务执行器：用 Roslyn C# Scripting 编译并运行 <c>scriptTask</c> 的脚本。
/// <para>
/// 设计要点：
/// · 只支持 <c>scriptFormat="csharp"</c>（本子集不内置 JavaScript 引擎，其余格式响亮失败）；
/// · 编译结果按脚本原文缓存（<see cref="Script"/> 可携带不同 globals 重复运行）；
/// · 脚本与委托同处一个命令事务（§5.3），内部<b>不得</b> await 外部系统 IO。
/// </para>
/// </summary>
internal static class ScriptTaskEngine
{
    /// <summary>文档化的脚本格式（BPMN 里大小写不敏感地接受 csharp / c#）。</summary>
    internal static readonly string[] AcceptedFormats = ["csharp", "c#"];

    private static readonly ConcurrentDictionary<string, RoslynScript> ScriptCache =
        new(StringComparer.Ordinal);

    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(
            typeof(object).Assembly,
            typeof(System.Linq.Enumerable).Assembly,
            typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly,
            typeof(IDelegateExecution).Assembly,
            typeof(NetBpm.Bpmn.Model.IFlowElement).Assembly,
            typeof(NetBpm.El.IVariableScope).Assembly)
        .WithImports(
            "System",
            "System.Collections.Generic",
            "System.Globalization",
            "System.IO",
            "System.Linq",
            "System.Text",
            "System.Threading",
            "System.Threading.Tasks",
            "NetBpm.Engine.Execution");

    /// <summary>执行脚本并把结果写回流程变量（resultVariable / autoStoreVariables）。</summary>
    public static async ValueTask ExecuteAsync(
        ScriptTask scriptTask,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scriptTask);
        ArgumentNullException.ThrowIfNull(execution);

        if (string.IsNullOrWhiteSpace(scriptTask.Script))
        {
            throw new EngineException($"脚本任务 '{scriptTask.Id}' 没有脚本内容（<script> 元素为空）。");
        }

        var format = scriptTask.ScriptFormat?.Trim();
        if (format is null ||
            !AcceptedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
        {
            throw new EngineException(
                $"脚本任务 '{scriptTask.Id}' 的 scriptFormat 不支持（实际：'{scriptTask.ScriptFormat ?? "(未指定)"}'）。" +
                "本子集只支持 csharp（Roslyn Scripting）。");
        }

        var script = ScriptCache.GetOrAdd(
            scriptTask.Script,
            static code => CSharpScript.Create(code, Options, typeof(ScriptTaskGlobals)));

        var globals = new ScriptTaskGlobals
        {
            execution = execution,
            vars = await BuildVariableSnapshotAsync(execution, ct).ConfigureAwait(false),
        };

        RoslynScriptState state;
        try
        {
            state = await script.RunAsync(globals, ct).ConfigureAwait(false);
        }
        catch (CompilationErrorException ex)
        {
            throw new EngineException($"脚本任务 '{scriptTask.Id}' 编译失败：{ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not EngineException)
        {
            // 与 Activiti 一致：运行期脚本异常包装成引擎异常上抛（不吞错，事务随之回滚）
            throw new EngineException($"脚本任务 '{scriptTask.Id}' 执行失败：{ex.Message}", ex);
        }

        // activiti:autoStoreVariables="true"：把脚本顶层声明的变量全部存回流程
        if (scriptTask.AutoStoreVariables)
        {
            foreach (var variable in state.Variables)
            {
                if (variable.Name is "globals" or "execution" or "vars")
                {
                    continue;
                }

                await execution
                    .SetVariableAsync(variable.Name, variable.Value, ct)
                    .ConfigureAwait(false);
            }
        }

        // activiti:resultVariable：存脚本最后一条顶层表达式的值（C# 脚本的 ReturnValue）
        if (!string.IsNullOrEmpty(scriptTask.ResultVariable))
        {
            await execution
                .SetVariableAsync(scriptTask.ResultVariable!, state.ReturnValue, ct)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<ExpandoObject> BuildVariableSnapshotAsync(
        IDelegateExecution execution,
        CancellationToken ct)
    {
        var snapshot = new ExpandoObject();
        var members = (IDictionary<string, object?>)snapshot;

        foreach (var (name, value) in await execution.GetVariablesAsync(ct).ConfigureAwait(false))
        {
            members[name] = value;
        }

        return snapshot;
    }
}
