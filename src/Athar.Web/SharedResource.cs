using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Athar.Web;

/// <summary>Marker for the UI strings in Resources/SharedResource(.ar).resx.</summary>
public sealed class SharedResource;

/// <summary>MudBlazor's built-in strings (Close, Clear, …) from our resx, so they follow the UI language; missing keys keep MudBlazor's English.</summary>
public sealed class AtharMudLocalizer(IStringLocalizer<SharedResource> l) : MudLocalizer
{
    public override LocalizedString this[string key] => l[key];
}
