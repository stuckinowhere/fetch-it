using Avalonia.Controls;
using Avalonia.Interactivity;
using FetchIt.Models;

namespace FetchIt.Views;

public partial class DuplicateWindow : Window
{
    public DuplicateWindow() : this("Downloads", ["file.jpg"])
    {
    }

    public DuplicateWindow(string folderLabel, IReadOnlyList<string> names)
    {
        InitializeComponent();
        AppIcons.ApplyToWindow(this);
        NativeWindowIcon.Bind(this);
        Body.Text = Describe(folderLabel, names);
    }

    internal static string Describe(string folderLabel, IReadOnlyList<string> names)
    {
        var count = names.Count;
        var listed = names.Take(5).ToList();
        var extra = count - listed.Count;
        var files = string.Join("\n", listed);
        if (extra > 0)
            files += extra == 1 ? "\n+1 more" : $"\n+{extra} more";

        var lead = count == 1
            ? $"This file is already in {folderLabel}:"
            : $"These {count} files are already in {folderLabel}:";
        return $"{lead}\n\n{files}\n\nSkip, keep both copies, or replace them?";
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e) => Close(DuplicateChoice.Skip);

    private void OnKeepBothClick(object? sender, RoutedEventArgs e) => Close(DuplicateChoice.KeepBoth);

    private void OnReplaceClick(object? sender, RoutedEventArgs e) => Close(DuplicateChoice.Overwrite);
}
