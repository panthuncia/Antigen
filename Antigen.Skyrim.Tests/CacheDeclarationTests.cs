using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Antigen.SDK.Analyzers;
using Antigen.SDK.Caches;
using Shouldly;
using Xunit;

namespace Antigen.Skyrim.Tests;

/// <summary>
/// Every analyzer and cache constructor declares the caches it resolves (<see cref="IUsesCaches"/>), so a host making
/// the caches first, and running each analyzer only once its caches are made, never leaves one waiting for a cache.
/// Found from their code: each call resolving a cache, in their methods, lambdas and local functions.
/// </summary>
public class CacheDeclarationTests
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    [Fact]
    public void Analyzers_and_caches_declare_the_caches_they_resolve()
    {
        var types = typeof(SkyrimAnalyzerModule).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && (typeof(IAnalyzer).IsAssignableFrom(t) || typeof(ICacheConstructor).IsAssignableFrom(t)));
        var problems = new List<string>();
        foreach (var type in types)
        {
            var resolved = Resolved(type);
            var declared = typeof(IUsesCaches).IsAssignableFrom(type)
                ? ((IUsesCaches)RuntimeHelpers.GetUninitializedObject(type)).Caches.ToHashSet()
                : [];
            foreach (var missing in resolved.Except(declared)) problems.Add($"{type.Name} resolves {missing.Name} without declaring it");
            foreach (var extra in declared.Except(resolved)) problems.Add($"{type.Name} declares {extra.Name} without resolving it");
        }
        problems.ShouldBeEmpty();
    }

    /// <summary>The caches a type's code resolves: in its own methods and those of the types nested in it (closures).</summary>
    private static HashSet<Type> Resolved(Type type)
    {
        var found = new HashSet<Type>();
        foreach (var nested in new[] { type }.Concat(Nested(type)))
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var method in nested.GetMethods(all).Cast<MethodBase>().Concat(nested.GetConstructors(all)))
            {
                foreach (var called in Calls(method))
                {
                    if (called is MethodInfo { IsGenericMethod: true } generic
                        && generic.Name is "ResolveCache" or "Resolve"
                        && (generic.DeclaringType == typeof(IProvideCaches) || generic.DeclaringType?.Namespace == typeof(IAnalyzer).Namespace))
                    {
                        found.Add(generic.GetGenericArguments()[0]);
                    }
                }
            }
        }
        return found;
    }

    private static IEnumerable<Type> Nested(Type type) =>
        type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(n => new[] { n }.Concat(Nested(n)));

    /// <summary>The methods a method's body calls, read from its IL.</summary>
    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var at = 0; at < il.Length;)
        {
            short value = il[at++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[at++]));
            var code = OpCodesByValue[value];
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
                _ => 4,
            };
            if (code.OperandType == OperandType.InlineMethod)
            {
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(BitConverter.ToInt32(il, at), typeArguments, methodArguments);
                }
                catch (ArgumentException)
                {
                }
                if (called is not null) yield return called;
            }
            at += size;
        }
    }
}