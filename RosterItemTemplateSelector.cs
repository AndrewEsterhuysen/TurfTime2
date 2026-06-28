using TurfTime2.Models;

namespace TurfTime2;

/// <summary>
/// Routes each item in the swipeable roster CollectionView to the correct
/// DataTemplate: either the normal swipeable player row or the
/// collapsible inactive-group header.
/// </summary>
public sealed class RosterItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PlayerTemplate        { get; set; }
    public DataTemplate? InactiveHeaderTemplate { get; set; }

    protected override DataTemplate? OnSelectTemplate(object item, BindableObject container)
        => item is InactiveGroupHeader
            ? InactiveHeaderTemplate
            : PlayerTemplate;
}
