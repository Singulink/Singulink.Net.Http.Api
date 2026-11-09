using System.Collections;
using System.Reflection;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Checks options classes against the options standard once they are frozen.
/// </summary>
internal static class OptionsAssertions
{
    /// <summary>
    /// Checks that the options are frozen: <c>IsFrozen</c> is <see langword="true"/>, every public setter throws, even with the value the property has,
    /// and every list is read-only.
    /// </summary>
    public static void ShouldBeFrozen(object options)
    {
        var type = options.GetType();
        type.GetProperty("IsFrozen")!.GetValue(options).ShouldBe(true, type.Name);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string name = $"{type.Name}.{property.Name}";
            object? value = property.GetValue(options);

            if (property.SetMethod is { IsPublic: true } setter)
            {
                var ex = Should.Throw<TargetInvocationException>(() => setter.Invoke(options, [value]), name);
                ex.InnerException.ShouldBeOfType<InvalidOperationException>(name).Message.ShouldBe(
                    $"The {type.Name} are in use, so they can no longer be changed.", name);
                property.GetValue(options).ShouldBe(value, name);
            }
            else if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(IList<>))
            {
                ((IList)value!).IsReadOnly.ShouldBeTrue(name);
            }
        }
    }
}
