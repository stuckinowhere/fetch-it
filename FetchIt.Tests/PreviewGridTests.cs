using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FetchIt.Models;
using FetchIt.ViewModels;
using FetchIt.Views;

namespace FetchIt.Tests;

/// <summary>Runs the real window with no screen, so it cannot open or take focus on the desktop.</summary>
public class PreviewGridTests
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(PreviewGridTests));

    private static MediaProbe Profile(int files) => new()
    {
        Title = "profile",
        Site = "Instagram",
        Engine = EngineKind.GalleryDl,
        ImageCount = files,
        Items = Enumerable.Range(0, files)
            .Select(i => new MediaItem { Kind = MediaKind.Image, Title = $"tile {i}", SaveName = $"{i}.jpg" })
            .ToList()
    };

    [Theory]
    [InlineData("Everything", true)]
    [InlineData("PhotosOnly", false)]
    public async Task The_media_question_closes_with_the_choice(string button, bool expected)
    {
        var answer = await Session.Dispatch(async () =>
        {
            var owner = new Window();
            owner.Show();
            var window = new MediaChoiceWindow(5979, 2005);
            var shown = window.ShowDialog<bool?>(owner);
            Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>(button)!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var result = await shown;
            owner.Close();
            return result;
        }, CancellationToken.None);

        Assert.Equal(expected, answer);
    }

    private static List<string> BuiltTiles(Window window)
        => window.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.GetType().Name == "Card")
            .Select(control => (control.DataContext as PreviewCard)?.Title)
            .OfType<string>()
            .ToList();

    private static void Settle(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    [Fact]
    public async Task Thousands_of_tiles_build_only_what_is_on_screen_and_scroll_to_the_end()
        => await Session.Dispatch(() =>
        {
            var viewModel = new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            try
            {
                viewModel.ApplyProbe(Profile(6000));
                Settle(window);

                var scroll = window.GetVisualDescendants()
                    .OfType<Control>()
                    .First(control => control.GetType().Name == "Card")
                    .FindAncestorOfType<ScrollViewer>()!;
                var first = BuiltTiles(window);
                Assert.Contains("tile 0", first);
                Assert.InRange(first.Count, 3, 60);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 100, $"extent {scroll.Extent.Height}");

                scroll.Offset = new Vector(0, scroll.Extent.Height / 2);
                Settle(window);
                var middle = BuiltTiles(window);
                Assert.DoesNotContain("tile 0", middle);
                Assert.InRange(middle.Count, 3, 60);

                scroll.Offset = new Vector(0, double.MaxValue);
                Settle(window);
                Assert.Contains("tile 5999", BuiltTiles(window));

                scroll.Offset = default;
                Settle(window);
                Assert.Contains("tile 0", BuiltTiles(window));
            }
            finally
            {
                // Not Close: that would save this window's position over the user's.
                window.Hide();
            }
        }, CancellationToken.None);

    [Fact]
    public async Task Every_button_without_words_has_a_name_and_the_save_folder_is_shown()
        => await Session.Dispatch(() =>
        {
            var viewModel = new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            try
            {
                Settle(window);
                var unnamed = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button => button.IsEffectivelyVisible)
                    .Where(button => !button.GetVisualDescendants().OfType<TextBlock>().Any(text => !string.IsNullOrWhiteSpace(text.Text)))
                    .Where(button => string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button)))
                    .ToList();
                Assert.Empty(unnamed);
                Assert.Contains(
                    window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text == viewModel.FolderLabel);
                Assert.False(string.IsNullOrWhiteSpace(
                    Avalonia.Automation.AutomationProperties.GetName(window.GetVisualDescendants().OfType<TextBox>().Single())));
            }
            finally
            {
                window.Hide();
            }
        }, CancellationToken.None);

    [Theory]
    [InlineData("alert")]
    [InlineData("quality")]
    [InlineData("update")]
    public async Task Escape_closes_the_dialog(string which)
    {
        var closed = await Session.Dispatch(() =>
        {
            var owner = new Window();
            owner.Show();
            Window dialog = which switch
            {
                "alert" => new AlertWindow("Error", "Something", true),
                "quality" => new QualityWindow(),
                _ => new UpdateWindow()
            };
            var isClosed = false;
            dialog.Closed += (_, _) => isClosed = true;
            _ = dialog.ShowDialog(owner);
            Dispatcher.UIThread.RunJobs();
            dialog.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            owner.Close();
            return isClosed;
        }, CancellationToken.None);

        Assert.True(closed);
    }

    [Fact]
    public async Task A_new_column_count_keeps_every_tile()
        => await Session.Dispatch(() =>
        {
            var viewModel = new MainViewModel();
            viewModel.ApplyProbe(Profile(7));

            viewModel.FitPreview(1200, 600);
            Assert.Equal(3, viewModel.PreviewRows.Count);

            viewModel.FitPreview(700, 600);
            Assert.Equal(4, viewModel.PreviewRows.Count);
            Assert.Equal(7, viewModel.PreviewRows.Sum(row => row.Cards.Count));
        }, CancellationToken.None);
}
