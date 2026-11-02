using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

// Port of UI/ChatSpace/GroupEditor.swift: create or edit a custom group ("nền tảng tuỳ chỉnh"), as a sheet.

public static class GroupEditor
{
    public static readonly string[] Symbols =
    [
        "folder.fill", "star.fill", "heart.fill", "briefcase.fill", "building.2.fill", "house.fill",
        "car.fill", "bolt.fill", "cart.fill", "bag.fill", "creditcard.fill", "dollarsign.circle.fill",
        "chart.bar.fill", "person.2.fill", "person.3.fill", "graduationcap.fill", "book.fill", "gamecontroller.fill",
        "airplane", "globe.asia.australia.fill", "flag.fill", "bell.fill", "megaphone.fill", "tag.fill",
        "shippingbox.fill", "wrench.and.screwdriver.fill", "hammer.fill", "leaf.fill", "flame.fill", "sparkles",
        "cup.and.saucer.fill", "fork.knife", "music.note", "camera.fill", "paintpalette.fill", "stethoscope",
        "bitcoinsign.circle.fill", "lock.fill", "shield.fill", "doc.text.fill", "envelope.fill", "message.fill",
    ];

    public static readonly uint[] Colors = [0x5E5CE6, 0x0A84FF, 0x32ADE6, 0x30D158, 0xFFD60A, 0xFF9F0A, 0xFF453A, 0xFF375F, 0xBF5AF2, 0x8E8E93];

    /// `.sheet(item:)` on the window that asked (the Settings window, otherwise the main window).
    public static void Present(CustomGroup group, Window? on = null)
    {
        Func<Action, Control> content = dismiss => new GroupEditorForm(group, dismiss).Scheme(Scheme.Light);
        on ??= ChatTools.ActiveWindow();
        if (on != null && ReferenceEquals(on, SettingsWindow.Window)) SettingsWindow.PresentSheet(content);
        else if (on is MacWindow mac) mac.PresentSheet(content);
        else (on as MainWindow ?? App.Current.MainWindow as MainWindow)?.PresentSheet(content);
    }
}

/// `@State var group` / `keywordText` of `GroupEditor`.
internal sealed class GroupDraft
{
    public readonly State<CustomGroup> Group;
    public readonly State<string> KeywordText;

    public GroupDraft(CustomGroup g)
    {
        Group = new State<CustomGroup>(g);
        KeywordText = new State<string>(string.Join(", ", g.Keywords));
    }

    public List<string> Keywords() =>
        KeywordText.Value.Split(',').Select(k => k.Trim(' ', '\t')).Where(k => k.Length > 0).ToList();

    public CustomGroup Draft()
    {
        var keywords = Keywords();
        var g = Group.Value with { Keywords = keywords };
        if (g.Name.Trim(' ', '\t').Length == 0) g = g with { Name = keywords.FirstOrDefault() ?? "" };
        return g;
    }

    public void Edit(Func<CustomGroup, CustomGroup> change) => Group.Value = change(Group.Value);
}

/// `GroupEditor` (600 × 700).
internal sealed class GroupEditorForm : Reactive
{
    private const double SheetWidth = 600, SheetHeight = 700;

    public GroupEditorForm(CustomGroup group, Action dismiss) : this(new GroupDraft(group), dismiss) { }

    private GroupEditorForm(GroupDraft d, Action dismiss) : base(() => Body(d, dismiss)) { }

    private static Control Body(GroupDraft d, Action dismiss)
    {
        var group = d.Group.Value;
        var isNew = Workspace.Shared.Group(group.Id) == null;
        var keywords = d.Keywords();
        var draft = d.Draft();
        var matches = keywords.Count == 0 ? [] : AppModel.Shared.GroupRefs(draft);

        void Save()
        {
            if (d.Keywords().Count == 0) return;
            Workspace.Shared.Upsert(d.Draft());
            AppModel.Shared.Rail = RailItem.Group(d.Group.Value.Id);
            dismiss();
        }

        var header = HStack(12,
                new GroupIcon(draft, 44),
                VStack(HAlign.Leading, 2,
                    Text(isNew ? "Nhóm tuỳ chỉnh mới" : "Sửa nhóm").Font(17, Weight.Bold),
                    Text("Gom mọi đoạn chat có tên khớp từ khoá, trên tất cả nền tảng, vào một mục riêng ở thanh bên.")
                        .Font(12).Foreground(SColor.Secondary)))
            .Padding(20);

        // MARK: Form

        var name = FormField("editor.name", "Tên", keywords.FirstOrDefault() ?? "VD: Dự án VF",
            new Binding<string>(() => d.Group.Value.Name, v => d.Edit(g => g with { Name = v })), Save);
        var keywordField = FormField("editor.keywords", "Từ khoá", "VF, VinFast", d.KeywordText, Save);

        var match = FormRow("Cách so khớp", FormPopUp(
            new Binding<CustomGroup.MatchKind>(() => d.Group.Value.Match, v => d.Edit(g => g with { Match = v })),
            [(CustomGroup.MatchKind.Contains, "Chứa từ khoá"),
             (CustomGroup.MatchKind.Prefix, "Bắt đầu bằng từ khoá"),
             (CustomGroup.MatchKind.Word, "Từ khoá đứng đầu một từ")]));

        var platforms = HStack(14, Platforms.All.Select(p =>
        {
            var binding = new Binding<bool>(
                () => d.Group.Value.Platforms.Count == 0 || d.Group.Value.Platforms.Contains(p),
                on => d.Edit(g =>
                {
                    var set = g.Platforms.Count == 0 ? Platforms.All.ToList() : g.Platforms.ToList();
                    if (on) { if (!set.Contains(p)) set.Add(p); } else set.RemoveAll(x => x == p);
                    return g with { Platforms = set.Count == Platforms.All.Length ? [] : set };
                }));
            var label = HStack(5, new PlatformGlyph(p, 16), Text(p.DisplayName()));
            return (Control?)HStack(6, new ToggleView(binding, false), label)
                .OnTap(() => binding.Value = !binding.Value).Cursor(Avalonia.Input.StandardCursorType.Arrow);
        }).ToArray()).FillWidth(Alignment.Leading);

        var cells = Enumerable.Range(0, (GroupEditor.Symbols.Length + 13) / 14)
            .Select(row => (Control?)HStack(6, GroupEditor.Symbols.Skip(row * 14).Take(14).Select(sym =>
            {
                var on = group.ImagePath == null && group.Symbol == sym;
                return (Control?)Button(() => d.Edit(g => g with { Symbol = sym, ImagePath = null }),
                    Image(sym).Font(14)
                        .Frame(30, 30)
                        .Foreground(on ? SColor.White : SColor.Primary)
                        .Background(RoundedRectangle(7).Fill(on ? SColor.Hex(group.ColorHex) : SColor.Primary.Opacity(0.06)))
                        .ContentShape());
            }).ToArray()))
            .ToArray();

        var colors = HStack(8,
            GroupEditor.Colors.Select(c => (Control?)Button(() => d.Edit(g => g with { ColorHex = c }),
                Circle().Fill(SColor.Hex(c)).Frame(20, 20)
                    .Overlay(Circle().StrokeBorder(SColor.Primary.Opacity(group.ColorHex == c ? 0.8 : 0), 2).Padding(-4))
                    .ContentShape())).Append(Spacer())
                .Append(MacButton.Titled("Chọn ảnh…", () => PickImage(d)))
                .Append(group.ImagePath != null ? MacButton.Titled("Bỏ ảnh", () => d.Edit(g => g with { ImagePath = null })) : null)
                .ToArray());

        Control previewBody;
        if (matches.Count == 0)
            previewBody = FormRows(FormRow(null, Text(keywords.Count == 0 ? "Nhập từ khoá để xem các đoạn chat sẽ được gom." : "Chưa có đoạn chat nào khớp.")
                .Foreground(SColor.Secondary)));
        else
        {
            var rows = matches.Take(8).Select(r => FormRow(null, HStack(8,
                new PlatformGlyph(r.Platform, 16),
                Text(AppModel.Shared.Chat(r)?.Title ?? r.ChatId).LineLimit(1), Spacer()))).ToList();
            if (matches.Count > 8) rows.Add(FormRow(null, Text($"và {matches.Count - 8} đoạn chat khác…").Foreground(SColor.Secondary)));
            previewBody = FormRows(rows.ToArray());
        }

        var form = VStack(HAlign.Leading, 20,
                FormSection(null, FormRows(name, keywordField, match),
                    Text("Phân cách nhiều từ khoá bằng dấu phẩy. Không phân biệt hoa thường và dấu: “VF” khớp “VF 1”, “VF-2”, “vf3”.")
                        .Font(10).Foreground(SColor.Secondary)),
                FormSection("Nền tảng", FormRows(FormRow(null, platforms))),
                FormSection("Biểu tượng",
                    FormRows(
                        FormRow(null, VStack(HAlign.Leading, 6, cells).FillWidth(Alignment.Leading)),
                        FormRow(null, colors))),
                FormSection($"Xem trước — {matches.Count} đoạn chat khớp", previewBody))
            .FillWidth(Alignment.Leading)
            .PaddingH(20).PaddingTop(4).PaddingBottom(20);

        var scroll = Memo("editor.scroll", () => new LegacyScrollHost(Clear));
        scroll.SetContent(form);

        // MARK: Buttons

        var save = MacButton.Titled(isNew ? "Tạo nhóm" : "Lưu", Save, ButtonKind.Prominent);
        save.IsEnabled = keywords.Count > 0;
        var buttons = HStack(8,
                !isNew
                    ? MacButton.Titled("Xoá nhóm", () =>
                    {
                        Workspace.Shared.Delete(d.Group.Value.Id);
                        dismiss();
                    }, ButtonKind.Destructive)
                    : null,
                Spacer(),
                MacButton.Titled("Huỷ", dismiss),
                save)
            .Padding(16);

        // 700 pt tall like the Swift sheet, but never taller than the window offers: an AppKit sheet may hang below its
        // parent, the Kit's is an in-window overlay, so the form gives up scroll height rather than its buttons.
        return VStack(HAlign.Leading, 0, header, new KeepHost(scroll).Fill(), buttons).Frame(width: SheetWidth, maxHeight: SheetHeight);
    }

    private static async void PickImage(GroupDraft d)
    {
        if (ChatTools.ActiveWindow()?.StorageProvider is not { } sp) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        if (Workspace.ImportGroupImage(path, d.Group.Value.Id) is { } imported)
            d.Edit(g => g with { ImagePath = imported });
    }

    // MARK: Grouped form chrome (`Form { }.formStyle(.grouped)`)

    /// A section: bold header, rounded card of rows, caption footer.
    private static Control FormSection(string? header, Control body, Control? footer = null) =>
        VStack(HAlign.Leading, 6,
                header != null ? Text(header).Font(13, Weight.Semibold).PaddingH(10) : null,
                body,
                footer?.PaddingH(10))
            .FillWidth(Alignment.Leading);

    /// Rows in one rounded card with hairlines between them.
    private static Control FormRows(params Control?[] rows)
    {
        var items = new List<Control?>();
        foreach (var row in rows.Where(r => r != null))
        {
            if (items.Count > 0) items.Add(Rectangle().Fill(SColor.Black.Opacity(0.1)).Frame(height: 0.5).PaddingH(10));
            items.Add(row);
        }
        return VStack(HAlign.Leading, 0, items.ToArray()).FillWidth(Alignment.Leading)
            .Background(RoundedRectangle(10).Fill(SColor.White))
            .Overlay(RoundedRectangle(10).StrokeBorder(SColor.Black.Opacity(0.08), 0.5));
    }

    /// One row: optional label on the leading side, the control filling the rest.
    private static Control FormRow(string? label, Control control) =>
        HStack(8,
                label != null ? Text(label) : null,
                label != null ? Spacer(8) : null,
                control)
            .PaddingH(10).PaddingV(6).Frame(minHeight: 30).FillWidth(Alignment.Leading);

    /// `TextField(label, text:, prompt:)` in a grouped form: label leading, text trailing.
    private static Control FormField(string key, string label, string prompt, Binding<string> text, Action submit)
    {
        var field = Keep(key, () =>
        {
            var f = new Field("", text, FieldStyle.Plain, font: SFont.System(13)) { OnSubmit = submit };
            f.Box.TextAlignment = Avalonia.Media.TextAlignment.Right;
            return f;
        });
        // The prompt follows the other fields (the name's is the first keyword), so it is set on every build.
        if (field is KeepHost { Child: Field kept } && kept.Children[^1] is TextBlock placeholder)
        {
            placeholder.Text = prompt;
            placeholder.HorizontalAlignment = HorizontalAlignment.Right;
            placeholder.SetValue(Env.ForegroundProperty, SColor.Black.Opacity(0.25));
        }
        return FormRow(label, field);
    }

    /// `Picker` in a grouped form: the chosen title and up/down chevrons, no bezel.
    private static Control FormPopUp<T>(Binding<T> selection, IReadOnlyList<(T value, string title)> options) =>
        View(() =>
        {
            var cur = selection.Value;
            var title = options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.value, cur)).title ?? "";
            Control? anchor = null;
            anchor = Button(() => Menus.Popup(anchor!, options.Select(o =>
                {
                    var v = o.value;
                    return MenuEntry.Item(o.title, () => selection.Value = v, EqualityComparer<T>.Default.Equals(v, Tracker.Untracked(() => selection.Value)));
                })),
                HStack(6, Text(title).Font(13).LineLimit(1), SettingsKit.UpDownChevrons(8).Foreground(SColor.Secondary)));
            return anchor;
        });
}
