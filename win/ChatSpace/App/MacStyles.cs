using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using ChatSpace.Kit;

namespace ChatSpace;

/// Restyles the few stock Avalonia controls ChatSpace uses (text fields, scroll bars, menus, tooltips)
/// to look like their AppKit counterparts instead of Fluent.
public static class MacStyles
{
    public static readonly FontFamily Ui = SFont.Body.AvaloniaFamily;

    public static Styles Create()
    {
        var s = new Styles();

        // Borderless text fields: every composer and search box draws its own background.
        s.Add(new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(Avalonia.Layout.Layoutable.MinHeightProperty, 0d),
                new Setter(TemplatedControl.FontFamilyProperty, Ui),
                new Setter(TemplatedControl.FontSizeProperty, 13d),
                new Setter(TextBox.CaretBrushProperty, Brushes.Black),
                new Setter(TextBox.SelectionBrushProperty, new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x7A, 0xFF))),
                new Setter(TextBox.SelectionForegroundBrushProperty, Brushes.Black),
            },
        });
        foreach (var state in new[] { ":pointerover", ":focus", ":focus-within" })
        {
            s.Add(new Style(x => x.OfType<TextBox>().Class(state).Template().Name("PART_BorderElement"))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, Brushes.Transparent),
                    new Setter(Border.BorderThicknessProperty, new Thickness(0)),
                },
            });
        }
        s.Add(new Style(x => x.OfType<TextBox>().Template().Name("PART_BorderElement"))
        {
            Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent), new Setter(Border.BorderThicknessProperty, new Thickness(0)) },
        });
        s.Add(new Style(x => x.OfType<TextBox>().Template().Name("PART_Watermark"))
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0))) },
        });

        // Overlay scroll bars like macOS with "show scroll bars: automatically".
        s.Add(new Style(x => x.OfType<ScrollViewer>())
        {
            Setters = { new Setter(ScrollViewer.AllowAutoHideProperty, true) },
        });
        s.Add(new Style(x => x.OfType<ScrollBar>())
        {
            Setters = { new Setter(ScrollBar.AllowAutoHideProperty, true) },
        });

        // Menus: white rounded panel, 13 pt rows, accent highlight.
        s.Add(new Style(x => x.OfType<MenuFlyoutPresenter>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xF5, 0xF6, 0xF6, 0xF6))),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(7)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(5)),
                new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0))),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0.5)),
            },
        });
        s.Add(new Style(x => x.OfType<ContextMenu>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xF5, 0xF6, 0xF6, 0xF6))),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(7)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(5)),
                new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0))),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0.5)),
            },
        });
        s.Add(new Style(x => x.OfType<MenuItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontFamilyProperty, Ui),
                new Setter(TemplatedControl.FontSizeProperty, 13d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 3)),
                new Setter(Avalonia.Layout.Layoutable.MinHeightProperty, 22d),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0))),
            },
        });
        s.Add(new Style(x => x.OfType<MenuItem>().Class(":pointerover").Template().Name("PART_LayoutRoot"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF))) },
        });
        s.Add(new Style(x => x.OfType<MenuItem>().Class(":pointerover").Template().Name("PART_HeaderPresenter"))
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Brushes.White) },
        });
        s.Add(new Style(x => x.OfType<Separator>())
        {
            Setters =
            {
                new Setter(Avalonia.Layout.Layoutable.MarginProperty, new Thickness(10, 5)),
                new Setter(Separator.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0))),
            },
        });

        // Tooltips: small pale-yellow-free macOS help tags.
        s.Add(new Style(x => x.OfType<ToolTip>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0))),
                new Setter(TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0))),
                new Setter(TemplatedControl.FontFamilyProperty, Ui),
                new Setter(TemplatedControl.FontSizeProperty, 11d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 3)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0.5)),
                new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0))),
            },
        });
        return s;
    }
}
