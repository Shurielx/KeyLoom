using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>Picks the list icon for a sequence step.</summary>
public sealed class StepIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => Application.Current?.TryFindResource(value switch
    {
        MacroActionType.MouseClick => "IconMouse",
        MacroActionType.KeyPress => "IconKeyboard",
        MacroActionType.MouseDown or MacroActionType.KeyDown => "IconHold",
        MacroActionType.MouseUp or MacroActionType.KeyUp => "IconArrowUp",
        MacroActionType.Delay => "IconClock",
        MacroActionType.MouseMove => "IconMove",
        MacroActionType.MousePosition => "IconTarget",
        MacroActionType.MouseScroll => "IconScroll",
        _ => "IconText"
    });
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
