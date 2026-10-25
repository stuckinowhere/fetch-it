# Product map

## Run

- Build and regression checks: `dotnet test FetchIt.sln -c Release --nologo` (Windows, .NET 8).
- Launch: `dotnet run --project FetchIt.csproj`.

## Surfaces

| Surface | Entry points | Verify |
| --- | --- | --- |
| Fetch preview and save | `Services/MediaFetcher.cs`, `Services/GalleryDlService.cs` | Preview limits never truncate full downloads; partial saves remain failures. |

## Flows that share state

- Preview metadata and direct download planning share the 50-item probe limit.
- Native GoFile and gallery-dl fallback share partial-save handling and duplicate-file choices.

## Do not ship without

- Windows regression suite passes, including GalleryDlService, GoFile and SaveClash cases.
- A partial GoFile save must not be retried by another engine over files already saved.
- Release tags must reference the tested, merged main commit.
