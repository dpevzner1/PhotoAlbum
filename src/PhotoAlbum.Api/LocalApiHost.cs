using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PhotoAlbum.Core.Domain;
using PhotoAlbum.Core.Interfaces;

namespace PhotoAlbum.Api;

/// <summary>
/// Minimal ASP.NET Core API running on 127.0.0.1:5150/api/v1/.
/// Start via LocalApiHost.StartAsync(); stop on app exit.
/// </summary>
public static class LocalApiHost
{
    private static WebApplication? _app;

    public static async Task StartAsync(
        IMediaItemRepository mediaRepo,
        IAlbumRepository albumRepo,
        ITagRepository tagRepo,
        IPersonRepository personRepo,
        IPlaceRepository placeRepo,
        IEventRepository eventRepo,
        IHiddenContentManager hidden = null,
        CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new() { Title = "PhotoAlbum Local API", Version = "v1",
                Description = "Local REST API for PhotoAlbum. All endpoints are loopback-only (127.0.0.1:5150)." });
        });

        builder.Services.AddSingleton(mediaRepo);
        builder.Services.AddSingleton(albumRepo);
        builder.Services.AddSingleton(tagRepo);
        builder.Services.AddSingleton(personRepo);
        builder.Services.AddSingleton(placeRepo);
        builder.Services.AddSingleton(eventRepo);
        if (hidden is not null)
            builder.Services.AddSingleton(hidden);

        // Bind only on loopback — never expose on LAN
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(k =>
            k.Listen(System.Net.IPAddress.Loopback, 5150));

        _app = builder.Build();

        _app.UseSwagger();
        _app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "PhotoAlbum API v1");
            c.RoutePrefix = "api/docs";
        });

        MapEndpoints(_app);

        await _app.StartAsync(ct);
    }

    public static async Task StopAsync()
    {
        if (_app is not null)
            await _app.StopAsync();
    }

    private static void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .WithOpenApi();

        // ── Health ──────────────────────────────────────────────────────────
        api.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTime.UtcNow }))
            .WithName("Health")
            .WithSummary("Liveness check");

        // ── Media ────────────────────────────────────────────────────────────
        api.MapGet("/media", async (
                IMediaItemRepository repo,
                int page = 0,
                int pageSize = 50,
                string? search = null,
                string? type = null,
                int? minRating = null,
                bool? favorite = null,
                bool? isHidden = null,
                bool includeDeleted = false,
                bool onlyDeleted = false,
                DateTime? capturedAfter = null,
                DateTime? capturedBefore = null,
                string? tagIds = null,
                string? personIds = null,
                string? placeIds = null,
                string? eventIds = null,
                string? albumIds = null,
                string? years = null) =>
            {
                MediaType? mediaType = type?.ToLowerInvariant() switch
                {
                    "photo" => MediaType.Photo,
                    "video" => MediaType.Video,
                    "unknown" => MediaType.Unknown,
                    _ => null
                };

                var filter = new MediaFilter(
                    SearchText:     search,
                    MediaType:      mediaType,
                    MinRating:      minRating,
                    IsFavorite:     favorite,
                    IsHidden:       isHidden,
                    IncludeDeleted: includeDeleted,
                    OnlyDeleted:    onlyDeleted,
                    CapturedAfter:  capturedAfter,
                    CapturedBefore: capturedBefore,
                    TagIds:         ParseLongCsv(tagIds),
                    PersonIds:      ParseLongCsv(personIds),
                    PlaceIds:       ParseLongCsv(placeIds),
                    EventIds:       ParseLongCsv(eventIds),
                    AlbumIds:       ParseLongCsv(albumIds),
                    Years:          ParseIntCsv(years),
                    Page:           page,
                    PageSize:       Math.Min(pageSize, 200));

                var items = await repo.QueryAsync(filter);
                var count = await repo.CountAsync(filter);
                return Results.Ok(new { total = count, page, items });
            })
            .WithName("GetMedia")
            .WithSummary("Query media library. Filters: type, minRating, favorite, isHidden, includeDeleted, onlyDeleted, capturedAfter, capturedBefore. Relationship filters (comma-separated IDs, OR within a list, AND across lists): tagIds, personIds, placeIds, eventIds, albumIds. Also years (comma-separated, e.g. 2023,2024).");

        api.MapGet("/media/{id:long}", async (long id, IMediaItemRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                return item is null ? Results.NotFound() : Results.Ok(item);
            })
            .WithName("GetMediaById")
            .WithSummary("Get single media item");

        api.MapPatch("/media/{id:long}/rating", async (
                long id, RatingPatch body, IMediaItemRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null) return Results.NotFound();
                item.Rating = Math.Clamp(body.Rating, 0, 5);
                await repo.UpdateAsync(item);
                return Results.Ok(item);
            })
            .WithName("SetRating")
            .WithSummary("Set star rating (0-5)");

        api.MapPatch("/media/{id:long}/favorite", async (
                long id, FavoritePatch body, IMediaItemRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null) return Results.NotFound();
                item.IsFavorite = body.IsFavorite;
                await repo.UpdateAsync(item);
                return Results.Ok(item);
            })
            .WithName("SetFavorite")
            .WithSummary("Mark/unmark as favorite");

        api.MapPatch("/media/{id:long}/notes", async (
                long id, NotesPatch body, IMediaItemRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null) return Results.NotFound();
                item.Notes = body.Notes;
                await repo.UpdateAsync(item);
                return Results.Ok(item);
            })
            .WithName("SetNotes")
            .WithSummary("Set or clear the notes/caption on a media item");

        api.MapPatch("/media/{id:long}/rotation", async (
                long id, RotationPatch body, IMediaItemRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null) return Results.NotFound();
                // Normalise to 0 / 90 / 180 / 270
                var deg = ((body.Degrees % 360) + 360) % 360;
                await repo.SetRotationAsync(id, deg);
                return Results.Ok(new { id, rotationDegrees = deg });
            })
            .WithName("SetRotation")
            .WithSummary("Set clockwise rotation in degrees (0, 90, 180, 270)");

        api.MapDelete("/media/{id:long}", async (long id, IMediaItemRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null) return Results.NotFound();
                await repo.SoftDeleteAsync(id, DeleteMode.Trash);
                return Results.NoContent();
            })
            .WithName("DeleteMedia")
            .WithSummary("Move media item to trash (soft delete)");

        api.MapPost("/media/{id:long}/restore", async (long id, IMediaItemRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null) return Results.NotFound();
                await repo.RestoreAsync(id);
                return Results.Ok(await repo.GetByIdAsync(id));
            })
            .WithName("RestoreMedia")
            .WithSummary("Restore a trashed media item back to the library");

        api.MapDelete("/media/{id:long}/hard", async (long id, IMediaItemRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null) return Results.NotFound();
                await repo.HardDeleteAsync(id);
                return Results.NoContent();
            })
            .WithName("HardDeleteMedia")
            .WithSummary("Permanently delete a media item from the database (irreversible)");

        api.MapGet("/media/{id:long}/tags", async (long id, ITagRepository repo) =>
                Results.Ok(await repo.GetTagsForMediaAsync(id)))
            .WithName("GetMediaTags")
            .WithSummary("Get tags applied to a media item");

        api.MapGet("/media/{id:long}/people", async (long id, IPersonRepository repo) =>
                Results.Ok(await repo.GetPeopleForMediaAsync(id)))
            .WithName("GetMediaPeople")
            .WithSummary("Get people tagged in a media item");

        api.MapGet("/media/{id:long}/albums", async (long id, IAlbumRepository repo) =>
                Results.Ok(await repo.GetAlbumsForMediaAsync(id)))
            .WithName("GetMediaAlbums")
            .WithSummary("Get albums that contain a media item");

        api.MapGet("/media/{id:long}/places", async (long id, IPlaceRepository repo) =>
                Results.Ok(await repo.GetPlacesForMediaAsync(id)))
            .WithName("GetMediaPlaces")
            .WithSummary("Get places associated with a media item");

        // ── Albums ──────────────────────────────────────────────────────────
        api.MapGet("/albums", async (IAlbumRepository repo) =>
                Results.Ok(await repo.GetAllAsync()))
            .WithName("GetAlbums")
            .WithSummary("List all albums");

        api.MapGet("/albums/{id:long}", async (long id, IAlbumRepository repo) =>
            {
                var album = await repo.GetByIdAsync(id);
                return album is null ? Results.NotFound() : Results.Ok(album);
            })
            .WithName("GetAlbumById")
            .WithSummary("Get single album");

        api.MapPost("/albums", async (AlbumBody body, IAlbumRepository repo) =>
            {
                var id = await repo.InsertAsync(new Album { Name = body.Name });
                var created = await repo.GetByIdAsync(id);
                return Results.Created($"/api/v1/albums/{id}", created);
            })
            .WithName("CreateAlbum")
            .WithSummary("Create a new album");

        api.MapPut("/albums/{id:long}", async (long id, AlbumBody body, IAlbumRepository repo) =>
            {
                var album = await repo.GetByIdAsync(id);
                if (album is null) return Results.NotFound();
                album.Name = body.Name;
                await repo.UpdateAsync(album);
                return Results.Ok(album);
            })
            .WithName("RenameAlbum")
            .WithSummary("Rename an album");

        api.MapDelete("/albums/{id:long}", async (long id, IAlbumRepository repo) =>
            {
                var album = await repo.GetByIdAsync(id);
                if (album is null) return Results.NotFound();
                await repo.DeleteAsync(id);
                return Results.NoContent();
            })
            .WithName("DeleteAlbum")
            .WithSummary("Delete an album (photos remain in library)");

        api.MapGet("/albums/{id:long}/media", async (long id, IAlbumRepository repo) =>
                Results.Ok(await repo.GetMediaIdsAsync(id)))
            .WithName("GetAlbumMedia")
            .WithSummary("List media IDs in an album");

        api.MapPost("/albums/{id:long}/media/{mediaId:long}", async (
                long id, long mediaId, IAlbumRepository repo) =>
            {
                await repo.AddMediaAsync(id, mediaId);
                return Results.NoContent();
            })
            .WithName("AddMediaToAlbum")
            .WithSummary("Add a media item to an album");

        api.MapDelete("/albums/{id:long}/media/{mediaId:long}", async (
                long id, long mediaId, IAlbumRepository repo) =>
            {
                await repo.RemoveMediaAsync(id, mediaId);
                return Results.NoContent();
            })
            .WithName("RemoveMediaFromAlbum")
            .WithSummary("Remove a media item from an album");

        // ── Tags / Labels ────────────────────────────────────────────────────
        api.MapGet("/tags", async (ITagRepository repo) =>
                Results.Ok(await repo.GetAllAsync()))
            .WithName("GetTags")
            .WithSummary("List all tags");

        api.MapGet("/tags/{id:long}", async (long id, ITagRepository repo) =>
            {
                var tag = await repo.GetByIdAsync(id);
                return tag is null ? Results.NotFound() : Results.Ok(tag);
            })
            .WithName("GetTagById")
            .WithSummary("Get single tag");

        api.MapPost("/tags", async (TagBody body, ITagRepository repo) =>
            {
                var id = await repo.InsertAsync(new Tag { Name = body.Name });
                var created = await repo.GetByIdAsync(id);
                return Results.Created($"/api/v1/tags/{id}", created);
            })
            .WithName("CreateTag")
            .WithSummary("Create a new tag");

        api.MapPut("/tags/{id:long}", async (long id, TagBody body, ITagRepository repo) =>
            {
                var tag = await repo.GetByIdAsync(id);
                if (tag is null) return Results.NotFound();
                tag.Name = body.Name;
                await repo.UpdateAsync(tag);
                return Results.Ok(tag);
            })
            .WithName("RenameTag")
            .WithSummary("Rename a tag");

        api.MapDelete("/tags/{id:long}", async (long id, ITagRepository repo) =>
            {
                var tag = await repo.GetByIdAsync(id);
                if (tag is null) return Results.NotFound();
                await repo.DeleteAsync(id);
                return Results.NoContent();
            })
            .WithName("DeleteTag")
            .WithSummary("Delete a tag (photos remain in library, untagged)");

        api.MapGet("/tags/{id:long}/media", async (long id, ITagRepository repo, IMediaItemRepository mediaRepo) =>
            {
                var tag = await repo.GetByIdAsync(id);
                if (tag is null) return Results.NotFound();
                var items = await mediaRepo.QueryAsync(new MediaFilter(TagIds: [id], PageSize: 100_000));
                return Results.Ok(items.Select(i => i.Id).ToList());
            })
            .WithName("GetTagMedia")
            .WithSummary("List media IDs that have this tag applied");

        api.MapPost("/media/{mediaId:long}/tags/{tagId:long}", async (
                long mediaId, long tagId, ITagRepository repo) =>
            {
                await repo.TagMediaAsync(mediaId, tagId);
                return Results.NoContent();
            })
            .WithName("TagMedia")
            .WithSummary("Apply a tag to a media item");

        api.MapDelete("/media/{mediaId:long}/tags/{tagId:long}", async (
                long mediaId, long tagId, ITagRepository repo) =>
            {
                await repo.UntagMediaAsync(mediaId, tagId);
                return Results.NoContent();
            })
            .WithName("UntagMedia")
            .WithSummary("Remove a tag from a media item");

        // ── People ───────────────────────────────────────────────────────────
        api.MapGet("/people", async (IPersonRepository repo) =>
                Results.Ok(await repo.GetAllAsync()))
            .WithName("GetPeople")
            .WithSummary("List all people");

        api.MapGet("/people/{id:long}", async (long id, IPersonRepository repo) =>
            {
                var person = await repo.GetByIdAsync(id);
                return person is null ? Results.NotFound() : Results.Ok(person);
            })
            .WithName("GetPersonById")
            .WithSummary("Get single person");

        api.MapPost("/people", async (PersonBody body, IPersonRepository repo) =>
            {
                var id = await repo.InsertAsync(new Person { Name = body.Name });
                var created = await repo.GetByIdAsync(id);
                return Results.Created($"/api/v1/people/{id}", created);
            })
            .WithName("CreatePerson")
            .WithSummary("Create a new person");

        api.MapPut("/people/{id:long}", async (long id, PersonBody body, IPersonRepository repo) =>
            {
                var person = await repo.GetByIdAsync(id);
                if (person is null) return Results.NotFound();
                person.Name = body.Name;
                await repo.UpdateAsync(person);
                return Results.Ok(person);
            })
            .WithName("RenamePerson")
            .WithSummary("Rename a person");

        api.MapDelete("/people/{id:long}", async (long id, IPersonRepository repo) =>
            {
                var person = await repo.GetByIdAsync(id);
                if (person is null) return Results.NotFound();
                await repo.DeleteAsync(id);
                return Results.NoContent();
            })
            .WithName("DeletePerson")
            .WithSummary("Remove a person (tagged photos remain in library, untagged)");

        api.MapGet("/people/{id:long}/media", async (long id, IPersonRepository repo) =>
                Results.Ok(await repo.GetMediaIdsAsync(id)))
            .WithName("GetPersonMedia")
            .WithSummary("List media IDs tagged with this person");

        api.MapPost("/media/{mediaId:long}/people/{personId:long}", async (
                long mediaId, long personId, IPersonRepository repo) =>
            {
                await repo.TagMediaAsync(mediaId, personId);
                return Results.NoContent();
            })
            .WithName("TagMediaWithPerson")
            .WithSummary("Tag a person in a media item");

        api.MapDelete("/media/{mediaId:long}/people/{personId:long}", async (
                long mediaId, long personId, IPersonRepository repo) =>
            {
                await repo.UntagMediaAsync(mediaId, personId);
                return Results.NoContent();
            })
            .WithName("UntagMediaPerson")
            .WithSummary("Remove a person tag from a media item");

        // ── Places ───────────────────────────────────────────────────────────
        api.MapGet("/places", async (IPlaceRepository repo) =>
                Results.Ok(await repo.GetAllAsync()))
            .WithName("GetPlaces")
            .WithSummary("List all places");

        api.MapGet("/places/{id:long}", async (long id, IPlaceRepository repo) =>
            {
                var place = await repo.GetByIdAsync(id);
                return place is null ? Results.NotFound() : Results.Ok(place);
            })
            .WithName("GetPlaceById")
            .WithSummary("Get single place");

        api.MapPost("/places", async (PlaceBody body, IPlaceRepository repo) =>
            {
                var id = await repo.InsertAsync(new Place { Name = body.Name, Latitude = body.Latitude, Longitude = body.Longitude });
                var created = await repo.GetByIdAsync(id);
                return Results.Created($"/api/v1/places/{id}", created);
            })
            .WithName("CreatePlace")
            .WithSummary("Create a new place");

        api.MapPut("/places/{id:long}", async (long id, PlaceBody body, IPlaceRepository repo) =>
            {
                var place = await repo.GetByIdAsync(id);
                if (place is null) return Results.NotFound();
                place.Name      = body.Name;
                place.Latitude  = body.Latitude;
                place.Longitude = body.Longitude;
                await repo.UpdateAsync(place);
                return Results.Ok(place);
            })
            .WithName("UpdatePlace")
            .WithSummary("Update place name and/or coordinates");

        api.MapDelete("/places/{id:long}", async (long id, IPlaceRepository repo) =>
            {
                var place = await repo.GetByIdAsync(id);
                if (place is null) return Results.NotFound();
                await repo.DeleteAsync(id);
                return Results.NoContent();
            })
            .WithName("DeletePlace")
            .WithSummary("Delete a place (photos remain in library, unassigned)");

        api.MapGet("/places/{id:long}/media", async (long id, IPlaceRepository repo) =>
            {
                var place = await repo.GetByIdAsync(id);
                if (place is null) return Results.NotFound();
                return Results.Ok(await repo.GetMediaIdsAsync(id));
            })
            .WithName("GetPlaceMedia")
            .WithSummary("List media IDs associated with a place");

        api.MapPost("/media/{mediaId:long}/places/{placeId:long}", async (
                long mediaId, long placeId, IPlaceRepository repo) =>
            {
                await repo.AssignMediaAsync(mediaId, placeId);
                return Results.NoContent();
            })
            .WithName("AssignMediaToPlace")
            .WithSummary("Associate a media item with a place");

        api.MapDelete("/media/{mediaId:long}/places/{placeId:long}", async (
                long mediaId, long placeId, IPlaceRepository repo) =>
            {
                await repo.UnassignMediaAsync(mediaId, placeId);
                return Results.NoContent();
            })
            .WithName("UnassignMediaFromPlace")
            .WithSummary("Remove a media item's association with a place");

        // ── Events ───────────────────────────────────────────────────────────
        api.MapGet("/events", async (IEventRepository repo) =>
                Results.Ok(await repo.GetAllAsync()))
            .WithName("GetEvents")
            .WithSummary("List all events");

        api.MapGet("/events/{id:long}", async (long id, IEventRepository repo) =>
            {
                var ev = await repo.GetByIdAsync(id);
                return ev is null ? Results.NotFound() : Results.Ok(ev);
            })
            .WithName("GetEventById")
            .WithSummary("Get single event");

        api.MapPost("/events", async (EventBody body, IEventRepository repo) =>
            {
                var id = await repo.InsertAsync(new Event
                {
                    Name        = body.Name,
                    Description = body.Description,
                    StartUtc    = body.StartUtc,
                    EndUtc      = body.EndUtc
                });
                var created = await repo.GetByIdAsync(id);
                return Results.Created($"/api/v1/events/{id}", created);
            })
            .WithName("CreateEvent")
            .WithSummary("Create a new event");

        api.MapPut("/events/{id:long}", async (long id, EventBody body, IEventRepository repo) =>
            {
                var ev = await repo.GetByIdAsync(id);
                if (ev is null) return Results.NotFound();
                ev.Name        = body.Name;
                ev.Description = body.Description;
                ev.StartUtc    = body.StartUtc;
                ev.EndUtc      = body.EndUtc;
                await repo.UpdateAsync(ev);
                return Results.Ok(ev);
            })
            .WithName("UpdateEvent")
            .WithSummary("Update event details");

        api.MapDelete("/events/{id:long}", async (long id, IEventRepository repo) =>
            {
                var ev = await repo.GetByIdAsync(id);
                if (ev is null) return Results.NotFound();
                await repo.DeleteAsync(id);
                return Results.NoContent();
            })
            .WithName("DeleteEvent")
            .WithSummary("Delete an event (photos remain in library, unassigned)");

        api.MapGet("/events/{id:long}/media", async (long id, IEventRepository repo) =>
            {
                var ev = await repo.GetByIdAsync(id);
                if (ev is null) return Results.NotFound();
                return Results.Ok(await repo.GetMediaIdsAsync(id));
            })
            .WithName("GetEventMedia")
            .WithSummary("List media IDs associated with an event");

        api.MapPost("/media/{mediaId:long}/events/{eventId:long}", async (
                long mediaId, long eventId, IEventRepository repo) =>
            {
                await repo.AssignMediaAsync(mediaId, eventId);
                return Results.NoContent();
            })
            .WithName("AssignMediaToEvent")
            .WithSummary("Associate a media item with an event");

        api.MapDelete("/media/{mediaId:long}/events/{eventId:long}", async (
                long mediaId, long eventId, IEventRepository repo) =>
            {
                await repo.UnassignMediaAsync(mediaId, eventId);
                return Results.NoContent();
            })
            .WithName("UnassignMediaFromEvent")
            .WithSummary("Remove a media item's association with an event");

        // ── Hidden content vault ─────────────────────────────────────────────
        api.MapGet("/hidden/status", (IHiddenContentManager hidden) =>
                Results.Ok(new
                {
                    isUnlocked     = hidden.IsUnlocked,
                    hasPin         = hidden.HasPin,
                    hiddenAlbumIds = hidden.HiddenAlbumIds.ToList(),
                    hiddenTagIds   = hidden.HiddenTagIds.ToList()
                }))
            .WithName("GetHiddenStatus")
            .WithSummary("Hidden vault lock state, hidden album/tag IDs. Returns available=false when the hidden vault is not configured.");

        api.MapPost("/hidden/unlock", async (UnlockBody body, IHiddenContentManager hidden) =>
            {
                bool ok = await hidden.TryUnlockAsync(body.Pin);
                return ok ? Results.Ok(new { isUnlocked = true })
                          : Results.Json(new { error = "Incorrect PIN." }, statusCode: 401);
            })
            .WithName("UnlockHidden")
            .WithSummary("Unlock the hidden vault. Body: { pin: string | null }. Omit or null pin when no PIN is set.");

        api.MapPost("/hidden/lock", (IHiddenContentManager hidden) =>
            {
                hidden?.Lock();
                return Results.NoContent();
            })
            .WithName("LockHidden")
            .WithSummary("Lock the hidden vault.");

        api.MapPost("/hidden/albums/{albumId:long}", async (long albumId, IHiddenContentManager hidden) =>
            {
                await hidden.HideAlbumAsync(albumId);
                return Results.NoContent();
            })
            .WithName("HideAlbum")
            .WithSummary("Add an album to the hidden vault. All photos in this album are excluded from Library queries when locked.");

        api.MapDelete("/hidden/albums/{albumId:long}", async (long albumId, IHiddenContentManager hidden) =>
            {
                await hidden.UnhideAlbumAsync(albumId);
                return Results.NoContent();
            })
            .WithName("UnhideAlbum")
            .WithSummary("Remove an album from the hidden vault.");

        api.MapPost("/hidden/tags/{tagId:long}", async (long tagId, IHiddenContentManager hidden) =>
            {
                await hidden.HideTagAsync(tagId);
                return Results.NoContent();
            })
            .WithName("HideTag")
            .WithSummary("Add a tag to the hidden vault. All photos bearing this tag are excluded from Library queries when locked.");

        api.MapDelete("/hidden/tags/{tagId:long}", async (long tagId, IHiddenContentManager hidden) =>
            {
                await hidden.UnhideTagAsync(tagId);
                return Results.NoContent();
            })
            .WithName("UnhideTag")
            .WithSummary("Remove a tag from the hidden vault.");
    }

    // ── Query helpers ──────────────────────────────────────────────────────────
    /// <summary>Parse a comma-separated list of longs (e.g. "1,4,7"); null/empty → null.</summary>
    private static IReadOnlyList<long>? ParseLongCsv(string? csv)
    {
        if (string.IsNullOrEmpty(csv)) return null;
        var parsed = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var v) ? (long?)v : null)
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return parsed.Count > 0 ? parsed : null;
    }

    /// <summary>Parse a comma-separated list of ints (e.g. "2023,2024"); null/empty → null.</summary>
    private static IReadOnlyList<int>? ParseIntCsv(string? csv)
    {
        if (string.IsNullOrEmpty(csv)) return null;
        var parsed = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? (int?)v : null)
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return parsed.Count > 0 ? parsed : null;
    }

    // ── Request bodies ───────────────────────────────────────────────────────
    private record RatingPatch(int Rating);
    private record UnlockBody(string? Pin);
    private record FavoritePatch(bool IsFavorite);
    private record NotesPatch(string? Notes);
    private record RotationPatch(int Degrees);
    private record AlbumBody(string Name);
    private record TagBody(string Name);
    private record PersonBody(string Name);
    private record PlaceBody(string Name, double? Latitude, double? Longitude);
    private record EventBody(string Name, string? Description, DateTime? StartUtc, DateTime? EndUtc);
}
