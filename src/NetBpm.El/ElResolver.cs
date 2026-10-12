using System.Collections;
using System.Globalization;
using System.Reflection;

namespace NetBpm.El;

/// <summary>
/// 解析链：标识符 → 属性 → 索引 → 方法调用。
/// 对照 Activiti 的 <c>VariableScopeElResolver</c> + jakarta.el 的
/// <c>MapELResolver</c> / <c>ListELResolver</c> / <c>ArrayELResolver</c> / <c>BeanELResolver</c>。
/// <para>
/// 本子集把这条链收敛成一个类，但**保留了两处会改变结果的边界**（与 JUEL 一致）：
/// <list type="bullet">
///   <item><description>属性/方法解析不出来是<b>响亮失败</b>（<see cref="PropertyNotFoundException"/> /
///   <see cref="MethodNotFoundException"/>）；</description></item>
///   <item><description>Map 缺键、List/数组越界是<b>静默 null</b>。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 已登记的偏差（§15.9）：
/// <list type="number">
///   <item><description><b>属性名不区分大小写</b>：Java 用 <c>getProcessInstanceId()</c>（小写开头），
///   而 .NET 属性是 <c>ProcessInstanceId</c>（PascalCase），照抄 Java 的区分大小写会让
///   <c>${execution.processInstanceId}</c> 这类写法全部解析失败。先精确匹配、再忽略大小写匹配；</description></item>
///   <item><description><b>方法调用不支持返回 <c>Task</c> 的异步方法</b>（会走"找不到方法"）；
///   但对变量作用域（<see cref="IVariableScope"/>）上的 <c>getVariable</c> 一族做了异步垫片；</description></item>
///   <item><description><b>Java 的 String / Collection 方法名有垫片</b>（<c>length()</c>/<c>size()</c>/<c>contains()</c>…），
///   因为 .NET 上没有同名成员。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class ElResolver
{
    /// <summary>隐式对象名（Activiti 的隐式对象里本子集只提供 <c>execution</c>）。</summary>
    private const string ExecutionIdentifier = "execution";

    /// <summary>标识符解析：隐式对象 → 流程变量；都没有则<b>响亮失败</b>。</summary>
    public static async ValueTask<object?> ResolveIdentifierAsync(
        ElContext context,
        string name,
        CancellationToken ct)
    {
        // 隐式对象优先（与 Activiti 的解析器顺序一致：execution 会遮蔽同名变量）
        if (string.Equals(name, ExecutionIdentifier, StringComparison.Ordinal))
        {
            return context.Scope;
        }

        if (await context.Scope.HasVariableAsync(name, ct).ConfigureAwait(false))
        {
            return await context.Scope.GetVariableAsync(name, ct).ConfigureAwait(false);
        }

        throw new PropertyNotFoundException($"无法解析标识符 '{name}'。");
    }

    /// <summary>属性解析（Map → POJO 属性/字段）。</summary>
    public static ValueTask<object?> ResolvePropertyAsync(
        ElContext context,
        object baseValue,
        string name,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseValue);
        ArgumentNullException.ThrowIfNull(name);

        switch (baseValue)
        {
            case IDictionary<string, object?> generic:
                return ValueTask.FromResult(generic.TryGetValue(name, out var genericValue) ? genericValue : null);

            case IReadOnlyDictionary<string, object?> readOnly:
                return ValueTask.FromResult(readOnly.TryGetValue(name, out var value) ? value : null);

            case IDictionary dictionary:
                return ValueTask.FromResult(dictionary.Contains(name) ? dictionary[name] : null);
        }

        var member = FindMember(baseValue.GetType(), name);

        if (member is null)
        {
            throw new PropertyNotFoundException(
                $"无法在 '{baseValue.GetType().Name}' 上解析属性 '{name}'。");
        }

        return ValueTask.FromResult(member(baseValue));
    }

    /// <summary>索引解析（List/数组按整数下标，Map 按键）。越界与缺键都是<b>静默 null</b>。</summary>
    public static ValueTask<object?> ResolveIndexAsync(
        ElContext context,
        object baseValue,
        object? index,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseValue);

        switch (baseValue)
        {
            // ⚠️ 刻意**不**支持字符串下标：JUEL 的解析器链（Array/List/Map/Bean）都不处理 String 索引，
            // `${'abc'[0]}` 在 JUEL 里是 PropertyNotFoundException。放宽会造出"假差异"。
            case IList list when TryToIndex(index, out var listIndex):
                return ValueTask.FromResult(listIndex >= 0 && listIndex < list.Count ? list[listIndex] : null);

            case Array array when TryToIndex(index, out var arrayIndex):
                return ValueTask.FromResult(arrayIndex >= 0 && arrayIndex < array.Length ? array.GetValue(arrayIndex) : null);
        }

        if (index is null)
        {
            return ValueTask.FromResult<object?>(null);
        }

        // Map / 字典：按字符串键取；键不是字符串时按属性名取（POJO 上的索引写法）
        if (index is string key)
        {
            return ResolvePropertyAsync(context, baseValue, key, ct);
        }

        var indexer = FindIndexer(baseValue.GetType());

        if (indexer is null)
        {
            throw new PropertyNotFoundException(
                $"无法用 '{JavaFormat.Describe(index)}' 索引 '{baseValue.GetType().Name}'。");
        }

        return ValueTask.FromResult(indexer(baseValue, index));
    }

    /// <summary>方法调用：变量作用域垫片 → Java String/Collection 垫片 → 反射调用。</summary>
    public static async ValueTask<object?> InvokeAsync(
        ElContext context,
        object baseValue,
        string method,
        object?[] arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseValue);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);

        if (baseValue is IVariableScope scope && await TryInvokeScopeAsync(scope, method, arguments, ct).ConfigureAwait(false) is var (scopeHandled, scopeValue))
        {
            if (scopeHandled)
            {
                return scopeValue;
            }
        }

        if (baseValue is string text)
        {
            if (TryInvokeString(text, method, arguments, out var textValue))
            {
                return textValue;
            }
        }

        if (TryInvokeCollection(baseValue, method, arguments, out var collectionValue))
        {
            return collectionValue;
        }

        return InvokeReflected(baseValue, method, arguments);
    }

    /// <summary>属性赋值（对应 <c>AstDot.setValue</c>）。</summary>
    public static ValueTask SetPropertyAsync(
        ElContext context,
        object baseValue,
        string name,
        object? value,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseValue);
        ArgumentNullException.ThrowIfNull(name);

        switch (baseValue)
        {
            case IDictionary<string, object?> generic:
                generic[name] = value;
                return ValueTask.CompletedTask;

            case IDictionary dictionary:
                dictionary[name] = value;
                return ValueTask.CompletedTask;
        }

        var property = baseValue.GetType().GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (property is null || !property.CanWrite)
        {
            throw new PropertyNotFoundException(
                $"'{baseValue.GetType().Name}' 上的属性 '{name}' 不存在或不可写。");
        }

        property.SetValue(baseValue, value);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 变量作用域的异步垫片：<c>getVariable</c> / <c>hasVariable</c> / <c>setVariable</c> / <c>getVariables</c>。
    /// <para>Activiti 里这些方法由 <c>BeanELResolver</c> 直接在 <c>VariableScope</c> 上反射调用；
    /// 本移植版这些方法是异步的，反射调不动，因此显式垫一层。</para>
    /// </summary>
    private static async ValueTask<(bool Handled, object? Value)> TryInvokeScopeAsync(
        IVariableScope scope,
        string method,
        object?[] arguments,
        CancellationToken ct)
    {
        switch (method)
        {
            case "getVariable" when arguments.Length == 1 && AsString(arguments[0]) is { } name:
                return (true, await scope.GetVariableAsync(name, ct).ConfigureAwait(false));

            case "hasVariable" when arguments.Length == 1 && AsString(arguments[0]) is { } name:
                return (true, await scope.HasVariableAsync(name, ct).ConfigureAwait(false));

            case "setVariable" when arguments.Length == 2 && AsString(arguments[0]) is { } name:
                await scope.SetVariableAsync(name, arguments[1], ct).ConfigureAwait(false);

                return (true, null);

            case "getVariables" when arguments.Length == 0:
                return (true, await scope.GetVariablesAsync(ct).ConfigureAwait(false));

            default:
                return (false, null);
        }
    }

    /// <summary>Java <c>String</c> 的方法垫片（名字与 .NET 不同，必须显式映射）。</summary>
    private static bool TryInvokeString(string text, string method, object?[] arguments, out object? result)
    {
        result = null;

        switch (method)
        {
            case "length" when arguments.Length == 0:
                result = (long)text.Length;
                return true;

            case "isEmpty" when arguments.Length == 0:
                result = text.Length == 0;
                return true;

            case "toString" when arguments.Length == 0:
                result = text;
                return true;

            case "trim" when arguments.Length == 0:
                result = text.Trim();
                return true;

            case "toUpperCase" when arguments.Length == 0:
                result = text.ToUpperInvariant();
                return true;

            case "toLowerCase" when arguments.Length == 0:
                result = text.ToLowerInvariant();
                return true;

            case "contains" when arguments.Length == 1 && AsString(arguments[0]) is { } contained:
                result = text.Contains(contained, StringComparison.Ordinal);
                return true;

            case "startsWith" when arguments.Length == 1 && AsString(arguments[0]) is { } prefix:
                result = text.StartsWith(prefix, StringComparison.Ordinal);
                return true;

            case "endsWith" when arguments.Length == 1 && AsString(arguments[0]) is { } suffix:
                result = text.EndsWith(suffix, StringComparison.Ordinal);
                return true;

            case "equals" when arguments.Length == 1:
                result = string.Equals(text, arguments[0] as string, StringComparison.Ordinal);
                return true;

            case "equalsIgnoreCase" when arguments.Length == 1 && arguments[0] is string other:
                result = string.Equals(text, other, StringComparison.OrdinalIgnoreCase);
                return true;

            case "indexOf" when arguments.Length == 1 && AsString(arguments[0]) is { } search:
                result = (long)text.IndexOf(search, StringComparison.Ordinal);
                return true;

            case "charAt" when arguments.Length == 1 && arguments[0] is not null:
                result = text[(int)TypeConverter.CoerceToLong(arguments[0])];
                return true;

            case "substring" when arguments.Length == 1:
                result = text[(int)TypeConverter.CoerceToLong(arguments[0])..];
                return true;

            case "substring" when arguments.Length == 2:
                {
                    var begin = (int)TypeConverter.CoerceToLong(arguments[0]);
                    var end = (int)TypeConverter.CoerceToLong(arguments[1]);

                    result = text[begin..end];
                    return true;
                }

            case "replace" when arguments.Length == 2:
                result = text.Replace(
                    TypeConverter.CoerceToString(arguments[0]),
                    TypeConverter.CoerceToString(arguments[1]),
                    StringComparison.Ordinal);

                return true;

            default:
                return false;
        }
    }

    /// <summary>Java <c>Collection</c> / <c>Map</c> 的方法垫片。</summary>
    private static bool TryInvokeCollection(object baseValue, string method, object?[] arguments, out object? result)
    {
        result = null;

        switch (baseValue)
        {
            // ⚠️ IDictionary 必须排在 ICollection 之前（前者是后者的子类型）
            case IDictionary dictionary:
                switch (method)
                {
                    case "get" when arguments.Length == 1 && AsString(arguments[0]) is { } key:
                        result = dictionary.Contains(key) ? dictionary[key] : null;
                        return true;

                    case "containsKey" when arguments.Length == 1 && AsString(arguments[0]) is { } key:
                        result = dictionary.Contains(key);
                        return true;

                    case "size" when arguments.Length == 0:
                        result = (long)dictionary.Count;
                        return true;

                    case "isEmpty" when arguments.Length == 0:
                        result = dictionary.Count == 0;
                        return true;
                }

                break;

            case ICollection collection:
                switch (method)
                {
                    case "size" when arguments.Length == 0:
                        result = (long)collection.Count;
                        return true;

                    case "isEmpty" when arguments.Length == 0:
                        result = collection.Count == 0;
                        return true;

                    case "contains" when arguments.Length == 1:
                        result = collection.Cast<object?>().Any(item => BooleanOperations.Eq(item, arguments[0]));
                        return true;

                    case "toArray" when arguments.Length == 0:
                        result = collection.Cast<object?>().ToArray();
                        return true;
                }

                break;
        }

        return false;
    }

    /// <summary>
    /// 反射调用：先精确匹配方法名，再忽略大小写；参数个数一致且可赋值。
    /// 返回 <c>Task</c> 的异步方法不参与匹配（求值器是同步反射）。
    /// </summary>
    private static object? InvokeReflected(object baseValue, string method, object?[] arguments)
    {
        var type = baseValue.GetType();
        var candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(candidate => string.Equals(candidate.Name, method, StringComparison.Ordinal))
            .Concat(
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => string.Equals(candidate.Name, method, StringComparison.OrdinalIgnoreCase)))
            .Where(candidate => !candidate.ContainsGenericParameters)
            .Where(candidate => !typeof(Task).IsAssignableFrom(candidate.ReturnType))
            .ToList();

        foreach (var candidate in candidates)
        {
            var parameters = candidate.GetParameters();

            if (parameters.Length != arguments.Length)
            {
                continue;
            }

            var converted = new object?[arguments.Length];
            var matches = true;

            for (var i = 0; i < parameters.Length; i++)
            {
                if (TryConvertArgument(arguments[i], parameters[i].ParameterType, out converted[i]))
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (!matches)
            {
                continue;
            }

            try
            {
                return candidate.Invoke(baseValue, converted);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw new ElException(
                    $"调用 '{type.Name}.{method}' 时抛出异常：{exception.InnerException.Message}",
                    exception.InnerException);
            }
        }

        throw new MethodNotFoundException(
            $"无法在 '{type.Name}' 上找到方法 '{method}'（参数个数 {arguments.Length}）。");
    }

    private static bool TryConvertArgument(object? argument, Type parameterType, out object? converted)
    {
        converted = null;

        if (argument is null)
        {
            // Java 的引用类型参数可以收 null；值类型不行
            if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
            {
                return false;
            }

            return true;
        }

        var target = Nullable.GetUnderlyingType(parameterType) ?? parameterType;

        if (target.IsInstanceOfType(argument))
        {
            converted = argument;
            return true;
        }

        // 数值拓宽（Java 的重载解析也允许一定范围的数值转换）
        if (TypeConverter.IsNumber(argument) && TypeConverter.IsNumber(Activator.CreateInstance(target)))
        {
            try
            {
                converted = Convert.ChangeType(argument, target, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>属性或公共字段读取器（先精确匹配，再忽略大小写）。</summary>
    private static Func<object, object?>? FindMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        var property = type.GetProperty(name, flags)
            ?? type.GetProperty(name, flags | BindingFlags.IgnoreCase);

        if (property is not null && property.CanRead && property.GetIndexParameters().Length == 0)
        {
            return instance => property.GetValue(instance);
        }

        var field = type.GetField(name, flags) ?? type.GetField(name, flags | BindingFlags.IgnoreCase);

        return field is null ? null : instance => field.GetValue(instance);
    }

    private static Func<object, object?, object?>? FindIndexer(Type type)
    {
        var property = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);

        if (property is null || property.GetIndexParameters().Length != 1)
        {
            return null;
        }

        return (instance, index) => property.GetValue(instance, [index])!;
    }

    private static string? AsString(object? value) => value as string;

    private static bool TryToIndex(object? value, out int index)
    {
        index = 0;

        if (value is null || !TypeConverter.IsNumber(value))
        {
            return false;
        }

        index = (int)TypeConverter.CoerceToLong(value);

        return true;
    }
}
