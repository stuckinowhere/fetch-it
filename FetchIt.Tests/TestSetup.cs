using System.Runtime.CompilerServices;
using FetchIt.Services;

// LocalizationTests switch L.Language; running classes in parallel would race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FetchIt.Tests;

internal static class TestSetup
{
    // Assertions are written against the English source strings, whatever the
    // Windows display language of the machine running the tests.
    [ModuleInitializer]
    internal static void UseEnglish() => L.Language = "en";
}
