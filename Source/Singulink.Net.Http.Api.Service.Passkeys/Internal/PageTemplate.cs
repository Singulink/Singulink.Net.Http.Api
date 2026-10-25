using System.Text;

namespace Singulink.Net.Http.Api.Service.Passkeys.Internal;

/// <summary>
/// A page template with <c>{{name}}</c> placeholders, split once so that rendering is a single pass: values are never searched for placeholders
/// themselves.
/// </summary>
internal sealed class PageTemplate
{
    private readonly List<(string Text, bool IsPlaceholder)> _parts = [];

    public PageTemplate(string template)
    {
        int position = 0;

        while (template.IndexOf("{{", position, StringComparison.Ordinal) is int start and >= 0)
        {
            int end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);

            if (end < 0)
                break;

            _parts.Add((template[position..start], false));
            _parts.Add((template[(start + 2)..end], true));
            position = end + 2;
        }

        _parts.Add((template[position..], false));
    }

    public string Render(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();

        foreach (var (text, isPlaceholder) in _parts)
            builder.Append(isPlaceholder ? values[text] : text);

        return builder.ToString();
    }
}
