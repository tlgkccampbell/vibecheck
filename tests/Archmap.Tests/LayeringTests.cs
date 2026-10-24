using System.Reflection;
using Archmap.Diffing;
using Xunit;

namespace Archmap.Tests;

public class LayeringTests
{
    /// <summary>
    /// Returns the types a type's declaration mentions: its base type and interfaces, and the types of its fields,
    /// properties, constructor and method parameters and method returns, with their generic arguments.
    /// </summary>
    private static IEnumerable<Type> Mentioned(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        IEnumerable<Type> direct = [
            .. type.GetInterfaces(),
            .. type.BaseType is null ? [] : new[] { type.BaseType },
            .. type.GetFields(all).Select(f => f.FieldType),
            .. type.GetProperties(all).Select(p => p.PropertyType),
            .. type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType),
            .. type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)),
        ];
        return direct.SelectMany(Expand);
    }

    private static IEnumerable<Type> Expand(Type t) =>
        t.HasElementType ? Expand(t.GetElementType()!) : t.IsGenericType ? t.GetGenericArguments().SelectMany(Expand).Prepend(t) : [t];

    [Fact]
    public void DiffingDoesNotDependOnReviewing()
    {
        var diffing = typeof(Git).Assembly.GetTypes().Where(t => t.Namespace == "Archmap.Diffing");

        var crossings = diffing.SelectMany(t => Mentioned(t).Where(m => m.Namespace == "Archmap.Reviewing").Select(m => $"{t.Name} → {m.Name}")).Distinct();

        Assert.Empty(crossings);
    }
}
