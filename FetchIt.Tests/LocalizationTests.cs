using FetchIt.Models;
using FetchIt.Services;

namespace FetchIt.Tests;

public class LocalizationTests
{
    private sealed class Spanish : IDisposable
    {
        private readonly string _was = L.Language;
        public Spanish() => L.Language = "es";
        public void Dispose() => L.Language = _was;
    }

    [Fact]
    public void English_is_pass_through()
    {
        L.Language = "en";
        Assert.Equal("Not a link.", L.Translate("Not a link."));
        Assert.Equal("+3 more", L.More(3));
        Assert.Equal("Download", L.Download);
    }

    [Fact]
    public void Spanish_translates_fixed_and_patterned_messages()
    {
        using var _ = new Spanish();
        Assert.Equal("No es un enlace.", L.Translate("Not a link."));
        Assert.Equal("Guardados 2 de 5 archivos.", L.Translate("Saved 2 of 5 files."));
        Assert.Equal("1.2 MB/s  ·  40 MB restantes", L.Translate("1.2 MB/s  ·  40 MB left"));
        Assert.Equal("+3 más", L.More(3));
        Assert.Equal("Descargar", L.Download);
        Assert.Equal("Guardado en Descargas.", L.Format("Saved to {0}.", "Descargas"));
    }

    [Fact]
    public void Unknown_text_is_left_alone()
    {
        using var _ = new Spanish();
        Assert.Equal("12 / 40", L.Translate("12 / 40"));
        Assert.Equal("", L.Translate(null));
    }

    [Fact]
    public void Media_summary_follows_language()
    {
        var probe = new MediaProbe { Site = "youtube", VideoCount = 2, ImageCount = 1 };
        L.Language = "en";
        Assert.Equal("youtube  ·  2 videos  ·  1 image", probe.Summary);
        using var _ = new Spanish();
        Assert.Equal("youtube  ·  2 vídeos  ·  1 imagen", probe.Summary);
        Assert.Equal("IMAGEN", new MediaItem { Kind = MediaKind.Image }.Overlay);
    }

    [Fact]
    public void Summary_says_when_the_count_is_only_a_preview()
    {
        var probe = new MediaProbe { Site = "Instagram", ImageCount = 50, MayHaveMore = true };
        L.Language = "en";
        Assert.Equal("Instagram  ·  50 images  ·  preview only, Download gets everything", probe.Summary);
        using var _ = new Spanish();
        Assert.Equal("Instagram  ·  50 imágenes  ·  solo vista previa, Descargar baja todo", probe.Summary);
        Assert.Equal("120 archivos", L.Translate("120 files"));
        Assert.Equal("Guardados 64 archivos, pero algunos no se pudieron leer.", L.Translate("Saved 64 files, but some could not be read."));
    }

    [Fact]
    public void Account_status_reads_in_both_languages()
    {
        L.Language = "en";
        Assert.Equal("Not signed in", FetchIt.Views.AccountWindow.Describe(new SessionStatus(SessionState.None, null)));
        Assert.Equal("Signed in", FetchIt.Views.AccountWindow.Describe(new SessionStatus(SessionState.Active, null)));
        using var _ = new Spanish();
        Assert.Equal("Sin sesión", FetchIt.Views.AccountWindow.Describe(new SessionStatus(SessionState.None, null)));
        Assert.Equal("La sesión caducó. Inicia sesión otra vez.", FetchIt.Views.AccountWindow.Describe(new SessionStatus(SessionState.Expired, DateTimeOffset.UnixEpoch)));
        Assert.StartsWith("Sesión iniciada hasta el ", FetchIt.Views.AccountWindow.Describe(new SessionStatus(SessionState.Active, DateTimeOffset.UnixEpoch.AddYears(60))));
    }

    [Fact]
    public void Engine_strings_stay_english_in_code()
    {
        using var _ = new Spanish();
        // Services compare these by value; only the display layer translates.
        Assert.Equal("Only public profiles.", MediaRouter.PublicOnlyMessage);
        Assert.Equal("Solo perfiles públicos.", L.Translate(MediaRouter.PublicOnlyMessage));
    }
}
