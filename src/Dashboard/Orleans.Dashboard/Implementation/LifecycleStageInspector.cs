using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Dashboard.Model;
using Orleans.Runtime;

#nullable disable
namespace Orleans.Dashboard.Implementation;

/// <summary>
/// Uses reflection to extract the registered lifecycle observers from a
/// <see cref="ISiloLifecycleSubject"/> so the dashboard can render them as a
/// dependency graph.
/// </summary>
/// <remarks>
/// The dashboard does not have <c>InternalsVisibleTo</c> access to the
/// runtime, so we walk the private state of <c>SiloLifecycleSubject</c> and
/// <c>LifecycleSubject</c> via reflection. The shape of the data
/// (<c>List&lt;MonitoredObserver&gt;</c> on the silo subject, with public
/// <c>Name</c>/<c>Stage</c>/<c>StageName</c> properties and a private
/// <c>observer</c> field) is stable and is the same data structure already
/// emitted to logs by <c>SiloLifecycleSubject.OnStart</c>.
/// </remarks>
internal static class LifecycleStageInspector
{
    private static readonly HashSet<int> NamedStageValues = BuildNamedStageValues();
    private static readonly Func<CancellationToken, Task> NoOpStart = _ => Task.CompletedTask;
    private static readonly Func<CancellationToken, Task> NoOpStop = _ => Task.CompletedTask;

    public static LifecycleStageInfo[] GetStages(ISiloLifecycleSubject lifecycle)
    {
        if (lifecycle is null)
        {
            return [];
        }

        // The silo's lifecycle subject keeps a separate `observers` field
        // (List<MonitoredObserver>) that carries richer information than the
        // base `subscribers` field. Prefer that, but fall back to the base list
        // if reflection fails.
        var monitored = TryReadField<System.Collections.IEnumerable>(lifecycle, "observers");
        if (monitored is not null)
        {
            return BuildStages(EnumerateMonitored(monitored));
        }

        var subscribers = TryReadField<System.Collections.IEnumerable>(lifecycle, "subscribers");
        if (subscribers is not null)
        {
            return BuildStages(EnumerateOrdered(subscribers));
        }

        return [];
    }

    private static LifecycleStageInfo[] BuildStages(IEnumerable<(int Stage, string StageName, string Name, object InnerObserver)> entries)
    {
        return entries
            .GroupBy(e => e.Stage)
            .OrderBy(g => g.Key)
            .Select(group => new LifecycleStageInfo
            {
                Stage = group.Key,
                StageName = group.Select(e => e.StageName).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? group.Key.ToString(),
                IsNamedStage = NamedStageValues.Contains(group.Key),
                Observers = group.Select(e => BuildObserver(e.Name, e.InnerObserver)).ToArray(),
            })
            .ToArray();
    }

    private static IEnumerable<(int Stage, string StageName, string Name, object InnerObserver)> EnumerateMonitored(System.Collections.IEnumerable monitored)
    {
        foreach (var item in monitored)
        {
            if (item is null) continue;
            var type = item.GetType();
            var stage = (int)(type.GetProperty("Stage")?.GetValue(item) ?? 0);
            var stageName = type.GetProperty("StageName")?.GetValue(item) as string;
            var name = type.GetProperty("Name")?.GetValue(item) as string;
            var inner = type.GetField("observer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(item);
            yield return (stage, stageName, name, inner);
        }
    }

    private static IEnumerable<(int Stage, string StageName, string Name, object InnerObserver)> EnumerateOrdered(System.Collections.IEnumerable ordered)
    {
        foreach (var item in ordered)
        {
            if (item is null) continue;
            var type = item.GetType();
            var stage = (int)(type.GetProperty("Stage")?.GetValue(item) ?? 0);
            var inner = type.GetProperty("Observer")?.GetValue(item);
            yield return (stage, null, null, inner);
        }
    }

    private static LifecycleObserverInfo BuildObserver(string name, object inner)
    {
        var info = new LifecycleObserverInfo
        {
            Name = name ?? "(unnamed)",
        };

        if (inner is null)
        {
            info.ObserverType = "(disposed)";
            return info;
        }

        // The base LifecycleSubject wraps every registration in an internal
        // OrderedObserver. The silo subject wraps that again in MonitoredObserver.
        // Unwrap one extra layer of MonitoredObserver if reflection returned it.
        if (inner.GetType().Name == "MonitoredObserver")
        {
            inner = inner.GetType().GetField("observer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(inner) ?? inner;
        }

        // Try to extract delegate targets/methods. The delegate-based
        // Subscribe overloads in LifecycleExtensions wrap the delegates in a
        // private `Observer` (with onStart/onStop fields) or `StartupObserver`
        // (with an `_onStart` field, OnStop is no-op).
        var innerType = inner.GetType();
        Delegate onStart = TryReadDelegateField(inner, innerType, "onStart") ?? TryReadDelegateField(inner, innerType, "_onStart");
        Delegate onStop = TryReadDelegateField(inner, innerType, "onStop") ?? TryReadDelegateField(inner, innerType, "_onStop");

        if (onStart is null && onStop is null && innerType.GetMethod("OnStart") is not null)
        {
            // Concrete ILifecycleObserver implementation.
            info.ObserverType = FormatType(innerType);
            info.HasOnStart = true;
            info.HasOnStop = true;
            info.OnStartMethod = $"{FormatType(innerType)}.OnStart";
            info.OnStopMethod = $"{FormatType(innerType)}.OnStop";
            return info;
        }

        if (onStart is not null)
        {
            info.HasOnStart = !IsNoOp(onStart);
            info.OnStartMethod = FormatDelegate(onStart);
        }

        if (onStop is not null)
        {
            info.HasOnStop = !IsNoOp(onStop);
            info.OnStopMethod = FormatDelegate(onStop);
        }

        info.ObserverType = ResolveOwningType(onStart, onStop) ?? FormatType(innerType);

        return info;
    }

    private static Delegate TryReadDelegateField(object owner, Type ownerType, string fieldName)
    {
        var field = ownerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(owner) as Delegate;
    }

    private static bool IsNoOp(Delegate d)
    {
        // We treat compiler-generated `_ => Task.CompletedTask` and the
        // shared no-op delegates as "no-op" purely so the UI can dim the
        // edge. Detecting compiler closures is best-effort.
        if (d is null) return true;
        if (ReferenceEquals(d, NoOpStart) || ReferenceEquals(d, NoOpStop)) return true;
        var method = d.Method;
        if (method.DeclaringType is { } declaring && declaring.Name.Contains("<>c"))
        {
            // Lambda. Without analyzing the IL we can't be sure, but it's
            // almost always a no-op when used in lifecycle wiring.
            return true;
        }
        return false;
    }

    private static string FormatDelegate(Delegate d)
    {
        if (d is null) return null;
        var method = d.Method;
        var declaring = method.DeclaringType;
        if (declaring is null) return CleanMethodName(method.Name);

        // For closures, walk up to the user-visible enclosing type.
        if (declaring.Name.StartsWith("<>c", StringComparison.Ordinal) && declaring.DeclaringType is not null)
        {
            declaring = declaring.DeclaringType;
        }

        var cleanedMethod = CleanMethodName(method.Name);
        var targetType = d.Target?.GetType();
        if (targetType is not null && targetType != declaring && !targetType.Name.StartsWith("<>", StringComparison.Ordinal))
        {
            return $"{FormatType(targetType)}.{cleanedMethod}";
        }
        return $"{FormatType(declaring)}.{cleanedMethod}";
    }

    /// <summary>
    /// Strips compiler-generated prefixes from method names produced by lambdas
    /// (<c>&lt;Outer&gt;b__N_N</c>) and local functions
    /// (<c>&lt;Outer&gt;g__Name|N_N</c>). Local functions retain their original
    /// name; anonymous lambdas are rendered as <c>&lt;lambda in Outer&gt;</c>.
    /// </summary>
    private static string CleanMethodName(string methodName)
    {
        if (string.IsNullOrEmpty(methodName) || methodName[0] != '<')
        {
            return methodName;
        }

        // Find the matching '>' for the leading '<', accounting for nested
        // angle brackets in explicit-interface implementations like
        // `<Orleans.ILifecycleParticipant<Orleans.Runtime.ISiloLifecycle>.Participate>g__Name|N`.
        var depth = 0;
        var endOfOuter = -1;
        for (var i = 0; i < methodName.Length; i++)
        {
            var c = methodName[i];
            if (c == '<') depth++;
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    endOfOuter = i;
                    break;
                }
            }
        }

        if (endOfOuter < 0)
        {
            return methodName;
        }

        var outer = methodName.AsSpan(1, endOfOuter - 1).ToString();
        // Reduce explicit-interface implementations like
        // `Orleans.ILifecycleParticipant<...>.Participate` down to just
        // the trailing method name (Participate).
        var lastDot = LastTopLevelDot(outer);
        if (lastDot >= 0)
        {
            outer = outer[(lastDot + 1)..];
        }

        var suffix = methodName.AsSpan(endOfOuter + 1);
        if (suffix.Length >= 3 && suffix[0] == 'g' && suffix[1] == '_' && suffix[2] == '_')
        {
            // <Outer>g__LocalName|N_N -> LocalName
            var rest = suffix[3..];
            var pipe = rest.IndexOf('|');
            var localName = pipe >= 0 ? rest[..pipe] : rest;
            return localName.ToString();
        }

        if (suffix.Length >= 3 && suffix[0] == 'b' && suffix[1] == '_' && suffix[2] == '_')
        {
            // <Outer>b__N_N -> <lambda in Outer>
            return $"<lambda in {outer}>";
        }

        return methodName;
    }

    private static int LastTopLevelDot(string value)
    {
        var depth = 0;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            var c = value[i];
            if (c == '>') depth++;
            else if (c == '<') depth--;
            else if (c == '.' && depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    private static string ResolveOwningType(Delegate onStart, Delegate onStop)
    {
        var d = onStart ?? onStop;
        if (d is null) return null;
        var target = d.Target?.GetType();
        if (target is not null && !target.Name.StartsWith("<>", StringComparison.Ordinal))
        {
            return FormatType(target);
        }
        var declaring = d.Method.DeclaringType;
        if (declaring is null) return null;
        if (declaring.Name.StartsWith("<>c", StringComparison.Ordinal) && declaring.DeclaringType is not null)
        {
            declaring = declaring.DeclaringType;
        }
        return FormatType(declaring);
    }

    private static string FormatType(Type type)
    {
        if (type is null) return null;
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        var genericArgs = string.Join(", ", type.GetGenericArguments().Select(FormatType));
        var name = type.Namespace is null ? type.Name : $"{type.Namespace}.{type.Name}";
        var tickIndex = name.IndexOf('`');
        if (tickIndex >= 0) name = name[..tickIndex];
        return $"{name}<{genericArgs}>";
    }

    private static T TryReadField<T>(object owner, string fieldName) where T : class
    {
        var type = owner.GetType();
        while (type is not null)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null && field.GetValue(owner) is T value)
            {
                return value;
            }
            type = type.BaseType;
        }
        return null;
    }

    private static HashSet<int> BuildNamedStageValues()
    {
        var values = new HashSet<int>();
        foreach (var field in typeof(ServiceLifecycleStage).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(int))
            {
                values.Add((int)field.GetRawConstantValue());
            }
        }
        return values;
    }
}
