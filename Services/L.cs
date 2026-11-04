using System.Globalization;
using System.Text.RegularExpressions;

namespace FetchIt.Services;

/// <summary>
/// UI strings. English is the source text and the key; Spanish is picked from
/// the Windows display language. Engine code keeps its English messages (some
/// are compared by value); <see cref="Translate"/> maps them at display time.
/// </summary>
public static class L
{
    public static string Language { get; set; } = Detect();

    public static bool IsSpanish => Language == "es";

    internal static string Detect()
    {
        try
        {
            var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return two.Equals("es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
        }
        catch
        {
            return "en";
        }
    }

    /// <summary>Translate a fixed UI string.</summary>
    public static string T(string english)
        => IsSpanish && Es.TryGetValue(english, out var es) ? es : english;

    /// <summary>Translate a runtime message that may carry numbers or names.</summary>
    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsSpanish)
            return text ?? "";
        if (Es.TryGetValue(text, out var exact))
            return exact;
        foreach (var (pattern, replacement) in Patterns)
        {
            if (pattern.IsMatch(text))
                return pattern.Replace(text, replacement);
        }

        return text;
    }

    public static string Format(string english, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, T(english), args);

    // Main window
    public static string AppTitle => "fetch it";
    public static string EmptyHint => T("Paste a link, then Fetch.");
    public static string SaveFolderTip => T("Save folder");
    public static string UpdatesTip => T("Updates");
    public static string ThemeTip => T("Theme");
    public static string Fetch => "Fetch";
    public static string Stop => T("Stop");
    public static string Download => T("Download");
    public static string OpenFolder => T("Open folder");
    public static string Downloads => T("Downloads");
    public static string Footer => "© 2026 stuckinowhere · MIT";
    public static string More(int count) => Format("+{0} more", count);

    // Dialogs
    public static string Error => T("Error");
    public static string Saved => T("Saved");
    public static string Ok => "OK";
    public static string Cancel => T("Cancel");
    public static string NotNow => T("Not now");
    public static string Clipboard => T("Clipboard");
    public static string Paste => T("Paste");
    public static string PasteThisLink => T("Paste this link?");
    public static string AlreadySaved => T("ALREADY SAVED");
    public static string Replace => T("Replace");
    public static string KeepBoth => T("Keep both");
    public static string Skip => T("Skip");
    public static string Quality => T("QUALITY");
    public static string InstagramSignIn => T("Sign in to Instagram. Chrome can stay open.");
    public static string AccountTip => T("Account");
    public static string Accounts => T("ACCOUNTS");
    public static string SignIn => T("Sign in");
    public static string SignOut => T("Sign out");
    public static string Close => T("Close");
    public static string AccountNote => T("Instagram only shows posts after you sign in. You stay signed in here. Your password is not saved.");
    public static string Later => T("Later");
    public static string Install => T("Install");
    public static string Open => T("Open");

    private static readonly Dictionary<string, string> Es = new(StringComparer.Ordinal)
    {
        ["Paste a link, then Fetch."] = "Pega un enlace y pulsa Fetch.",
        ["Save folder"] = "Carpeta de guardado",
        ["Updates"] = "Actualizaciones",
        ["Theme"] = "Tema",
        ["Stop"] = "Parar",
        ["Download"] = "Descargar",
        ["Open folder"] = "Abrir carpeta",
        ["Downloads"] = "Descargas",
        ["+{0} more"] = "+{0} más",
        ["Error"] = "Error",
        ["Saved"] = "Guardado",
        ["Cancel"] = "Cancelar",
        ["Not now"] = "Ahora no",
        ["Clipboard"] = "Portapapeles",
        ["Paste"] = "Pegar",
        ["Paste this link?"] = "¿Pegar este enlace?",
        ["ALREADY SAVED"] = "YA GUARDADO",
        ["Replace"] = "Reemplazar",
        ["Keep both"] = "Conservar ambos",
        ["Skip"] = "Omitir",
        ["QUALITY"] = "CALIDAD",
        ["Pick a quality for “{0}”."] = "Elige la calidad para “{0}”.",
        ["Sign in to Instagram. Chrome can stay open."] = "Inicia sesión en Instagram. Chrome puede seguir abierto.",
        ["Account"] = "Cuenta",
        ["ACCOUNTS"] = "CUENTAS",
        ["Sign in"] = "Iniciar sesión",
        ["Sign out"] = "Cerrar sesión",
        ["Close"] = "Cerrar",
        ["Signed in"] = "Sesión iniciada",
        ["Signed in until {0}"] = "Sesión iniciada hasta el {0}",
        ["Not signed in"] = "Sin sesión",
        ["Session expired. Sign in again."] = "La sesión caducó. Inicia sesión otra vez.",
        ["Instagram only shows posts after you sign in. You stay signed in here. Your password is not saved."] =
            "Instagram solo muestra publicaciones con la sesión iniciada. Aquí se queda iniciada. Tu contraseña no se guarda.",
        ["Later"] = "Más tarde",
        ["Install"] = "Instalar",
        ["Open"] = "Abrir",
        ["This file is already in {0}:"] = "Este archivo ya está en {0}:",
        ["These {0} files are already in {1}:"] = "Estos {0} archivos ya están en {1}:",
        ["Skip, keep both copies, or replace them?"] = "¿Omitir, conservar ambas copias o reemplazarlas?",
        ["+1 more"] = "+1 más",
        ["UPDATE AVAILABLE"] = "ACTUALIZACIÓN DISPONIBLE",
        ["UPDATE CHECK"] = "ACTUALIZACIONES",
        ["UP TO DATE"] = "AL DÍA",
        ["UPDATE FAILED"] = "ACTUALIZACIÓN FALLIDA",
        ["fetch it {0} is ready. Install now? The app will close, then open again."] =
            "fetch it {0} está lista. ¿Instalar ahora? La app se cerrará y volverá a abrirse.",
        ["fetch it {0} is ready. Open the release page to get the setup."] =
            "fetch it {0} está lista. Abre la página de la versión para descargar el instalador.",
        ["Could not check for updates. Try again when you are online."] =
            "No se pudo comprobar si hay actualizaciones. Inténtalo con conexión.",
        ["You are on {0}."] = "Tienes la versión {0}.",
        ["Installing…"] = "Instalando…",
        ["Downloading update…"] = "Descargando actualización…",

        // Status line
        ["Reading link…"] = "Leyendo enlace…",
        ["Reading public profile…"] = "Leyendo perfil público…",
        ["Saving…"] = "Guardando…",
        ["Getting tools…"] = "Preparando herramientas…",
        ["tools"] = "herramientas",
        ["Sign in to Instagram…"] = "Inicia sesión en Instagram…",
        ["Trying another reader…"] = "Probando otro lector…",
        ["Merging…"] = "Uniendo…",
        ["Finishing…"] = "Terminando…",
        ["Saved to {0}."] = "Guardado en {0}.",
        ["Already in {0}."] = "Ya está en {0}.",

        // Engine messages (English stays the in-code value)
        ["Not a link."] = "No es un enlace.",
        ["Fetch first."] = "Primero pulsa Fetch.",
        ["Windows blocked that folder. Pick another save folder."] = "Windows bloqueó esa carpeta. Elige otra carpeta.",
        ["That site took too long. Try again."] = "Ese sitio tardó demasiado. Inténtalo de nuevo.",
        ["Could not read that link."] = "No se pudo leer ese enlace.",
        ["Could not fetch that video."] = "No se pudo descargar ese vídeo.",
        ["Could not save those files."] = "No se pudieron guardar esos archivos.",
        ["Could not save that file."] = "No se pudo guardar ese archivo.",
        ["Could not check for updates."] = "No se pudo comprobar si hay actualizaciones.",
        ["Could not install yt-dlp or ffmpeg."] = "No se pudo instalar yt-dlp o ffmpeg.",
        ["Could not start the installer."] = "No se pudo iniciar el instalador.",
        ["Update installed. Close fetch it and open it again."] = "Actualización instalada. Cierra fetch it y vuelve a abrirlo.",
        ["UPDATE INSTALLED"] = "ACTUALIZACIÓN INSTALADA",
        ["Only public profiles."] = "Solo perfiles públicos.",
        ["Instagram hid the posts. Sign in when asked. Chrome can stay open."] =
            "Instagram ocultó las publicaciones. Inicia sesión cuando se pida. Chrome puede seguir abierto.",
        ["Paste a Bunkr file or album link, not the homepage."] = "Pega un enlace de archivo o álbum de Bunkr, no la portada.",
        ["That stream is protected."] = "Ese stream está protegido.",
        ["That site wants a signed-in session. Close Chrome, then paste the link again."] =
            "Ese sitio pide una sesión iniciada. Cierra Chrome y pega el enlace otra vez.",
        ["That site changed. Try again after an update."] = "Ese sitio cambió. Prueba tras una actualización.",
        ["GoFile dropped the connection. Try again."] = "GoFile cortó la conexión. Inténtalo de nuevo.",
        ["GoFile dropped the connection. Try Download again."] = "GoFile cortó la conexión. Pulsa Descargar otra vez.",
        ["GoFile is blocked on this network. Try a VPN."] = "GoFile está bloqueado en esta red. Prueba con una VPN.",
        ["Kemono's media servers are not responding. Try again later."] = "Los servidores de Kemono no responden. Inténtalo más tarde.",
        ["Not a GoFile link."] = "No es un enlace de GoFile.",
        ["Update file failed verification."] = "El archivo de actualización no pasó la verificación.",
        ["That release has no SHA256SUMS.txt, so it cannot be verified."] =
            "Esa versión no tiene SHA256SUMS.txt y no se puede verificar.",
        ["No compatible installer found for this release."] = "No hay instalador compatible en esa versión.",
        ["Could not install the update."] = "No se pudo instalar la actualización.",

        // Media summary
        ["1 video"] = "1 vídeo",
        ["{0} videos"] = "{0} vídeos",
        ["1 image"] = "1 imagen",
        ["{0} images"] = "{0} imágenes",
        ["IMAGE"] = "IMAGEN",
        ["VIDEO"] = "VÍDEO",
        ["less than 0.1 MB left"] = "menos de 0.1 MB restantes"
    };

    private static readonly (Regex Pattern, string Replacement)[] Patterns =
    [
        (new Regex(@"^Saved (\d+) of (\d+) files\.$", RegexOptions.CultureInvariant), "Guardados $1 de $2 archivos."),
        (new Regex(@"^Saved (\d+) files \((\d+) as previews\)\.", RegexOptions.CultureInvariant), "Guardados $1 archivos ($2 como vistas previas)."),
        (new Regex(@" MB left$", RegexOptions.CultureInvariant), " MB restantes"),
        (new Regex(@"^Saved to (.+)\.$", RegexOptions.CultureInvariant), "Guardado en $1."),
        (new Regex(@"^Already in (.+)\.$", RegexOptions.CultureInvariant), "Ya está en $1.")
    ];
}
