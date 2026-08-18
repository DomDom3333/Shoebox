using Shoebox.Web.Data;
using Shoebox.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Shoebox.Web.Api;

public static class MediaEndpoints
{
    public static void MapMediaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/p/{code}/media", UploadAsync).DisableAntiforgery();
        api.MapGet("/media/{id:guid}/thumb", ServeThumbAsync);
        api.MapGet("/media/{id:guid}/display", ServeDisplayAsync);
        api.MapGet("/media/{id:guid}/original", ServeOriginalAsync);
        api.MapDelete("/media/{id:guid}", DeleteMediaAsync);
        api.MapPost("/media/{id:guid}/reprocess", ReprocessAsync);
        api.MapPost("/media/{id:guid}/like", ToggleLikeAsync).DisableAntiforgery();
        api.MapGet("/p/{code}/zip", DownloadZipAsync);
        api.MapGet("/p/{code}/qr", QrCodeAsync);
    }

    /// <summary>
    /// Takes one upload. Every way this can fail answers with the reason in the body, in the
    /// same shape as a success: whatever the page shows the uploader has to come from here,
    /// because a client left to fill in the blank can only guess, and a guess reads as a
    /// network problem when the file was simply not one this box takes.
    /// </summary>
    private static async Task<IResult> UploadAsync(
        string code,
        HttpRequest request,
        AppDbContext db,
        PoolService pools,
        MediaService media,
        MediaHandlers handlers,
        PoolAccessService access,
        UploaderIdentity identity)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return UploadFailed(StatusCodes.Status404NotFound,
                "This box no longer exists — it may have expired.");
        }

        if (!access.CanView(request.HttpContext, pool))
        {
            return UploadFailed(StatusCodes.Status401Unauthorized,
                "This box is locked. Refresh the page and enter the password again.");
        }

        if (!request.HasFormContentType)
        {
            return UploadFailed(StatusCodes.Status400BadRequest, "Expected multipart form data.");
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync();
        }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException)
        {
            // The body ran past the request limit, so it was never read and nothing below has
            // seen the file. Left alone this surfaces as an unhandled exception and an HTML
            // error page, which says nothing at all about size.
            return UploadFailed(StatusCodes.Status413PayloadTooLarge,
                $"That file is larger than this server accepts — {handlers.Policy.Summary}.");
        }

        var uploaderName = form["uploaderName"].ToString().Trim();
        if (uploaderName.Length is 0 or > 80)
        {
            return UploadFailed(StatusCodes.Status400BadRequest,
                "Please tell us who you are (1-80 characters).");
        }

        if (form.Files.Count == 0)
        {
            return UploadFailed(StatusCodes.Status400BadRequest, "No files in upload.");
        }

        var uid = identity.GetOrCreateUid(request.HttpContext);
        identity.RememberName(request.HttpContext, uploaderName);

        var results = new List<UploadResult>();
        foreach (var file in form.Files)
        {
            results.Add(await media.SaveAsync(pool, file, uploaderName, uid, request.HttpContext.RequestAborted));
        }

        return Results.Ok(new { results });
    }

    /// <summary>An upload that failed outright, carrying why in the body the page reads.</summary>
    private static IResult UploadFailed(int statusCode, string error) =>
        Results.Json(new { error }, statusCode: statusCode);

    private static async Task<IResult> ServeThumbAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access, StoragePaths paths)
    {
        var media = await FindAccessibleMediaAsync(id, context, db, access);
        if (media is null)
        {
            return Results.NotFound();
        }

        var thumbPath = paths.ThumbFile(media.PoolId, media.Id);
        if (!media.HasThumbnail || !File.Exists(thumbPath))
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(thumbPath, "image/webp");
    }

    private static async Task<IResult> ServeDisplayAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access, StoragePaths paths)
    {
        var media = await FindAccessibleMediaAsync(id, context, db, access);
        if (media is null)
        {
            return Results.NotFound();
        }

        var displayPath = paths.DisplayFile(media.PoolId, media.Id);
        if (!media.HasThumbnail || !File.Exists(displayPath))
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(displayPath, "image/webp", enableRangeProcessing: true);
    }

    private static async Task<IResult> ServeOriginalAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access, StoragePaths paths,
        [FromQuery] bool download = false)
    {
        var media = await FindAccessibleMediaAsync(id, context, db, access);
        if (media is null)
        {
            return Results.NotFound();
        }

        var path = paths.OriginalFile(media.PoolId, media.Id, media.Extension);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(path, media.ContentType,
            fileDownloadName: download ? media.OriginalFileName : null,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> ReprocessAsync(
        Guid id, HttpContext context, AppDbContext db, MediaService media, PoolAccessService access)
    {
        var item = await db.Media.Include(m => m.Pool).FirstOrDefaultAsync(m => m.Id == id);
        if (item is null)
            return Results.NotFound();
        if (!access.IsAdmin(context, item.PoolId))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var ok = await media.ReprocessAsync(item, context.RequestAborted);
        return ok
            ? Results.Ok(new { status = "reprocessed" })
            : Results.UnprocessableEntity(new { error = "Could not render this file (original missing or unreadable)" });
    }

    private static async Task<IResult> DeleteMediaAsync(
        Guid id, HttpContext context, AppDbContext db, MediaService media,
        PoolAccessService access, UploaderIdentity identity)
    {
        var item = await db.Media.Include(m => m.Pool).FirstOrDefaultAsync(m => m.Id == id);
        if (item is null)
        {
            return Results.NotFound();
        }

        var isAdmin = access.IsAdmin(context, item.PoolId);
        var isOwner = item.UploaderUid == identity.GetOrCreateUid(context);
        if (!isAdmin && !isOwner)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        await media.DeleteAsync(item);
        return Results.NoContent();
    }

    private static async Task<IResult> ToggleLikeAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access, UploaderIdentity identity)
    {
        var media = await FindAccessibleMediaAsync(id, context, db, access);
        if (media is null)
        {
            return Results.NotFound();
        }

        var uid = identity.GetOrCreateUid(context);
        var existing = await db.Likes.FirstOrDefaultAsync(l => l.MediaId == id && l.UploaderUid == uid);

        bool liked;
        if (existing is null)
        {
            db.Likes.Add(new MediaLike { MediaId = id, UploaderUid = uid, CreatedAt = DateTime.UtcNow });
            liked = true;
        }
        else
        {
            db.Likes.Remove(existing);
            liked = false;
        }

        await db.SaveChangesAsync();

        var count = await db.Likes.CountAsync(l => l.MediaId == id);
        return Results.Ok(new { liked, count });
    }

    private static async Task<IResult> DownloadZipAsync(
        string code, HttpContext context, AppDbContext db, PoolService pools,
        PoolAccessService access, UploaderIdentity identity, ZipStreamService zip,
        [FromQuery] string mode = "all")
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return Results.NotFound();
        }

        if (!access.CanView(context, pool))
        {
            return Results.Unauthorized();
        }

        var query = db.Media.Where(m => m.PoolId == pool.Id);
        if (mode == "others")
        {
            var uid = identity.GetOrCreateUid(context);
            query = query.Where(m => m.UploaderUid != uid);
        }

        var items = await query.OrderBy(m => m.TakenAt ?? m.UploadedAt).ToListAsync();
        if (items.Count == 0)
        {
            return Results.NotFound();
        }

        // ZipArchive still flushes some entry metadata with synchronous writes even on the
        // async code path; permit sync IO for this response only so Kestrel doesn't abort it.
        var bodyControl = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        var zipName = $"{SafeFileName(pool.Name)}{(mode == "others" ? "_others" : "")}.zip";
        context.Response.ContentType = "application/zip";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{zipName}\"";
        await zip.WriteAsync(items, context.Response.Body, context.RequestAborted);
        return Results.Empty;
    }

    private static async Task<IResult> QrCodeAsync(
        string code, HttpContext context, PoolService pools, PoolAccessService access, ShareLinkService links)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return Results.NotFound();
        }

        if (!access.CanView(context, pool))
        {
            return Results.Unauthorized();
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(links.PoolUrl(context, pool), QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 10);

        context.Response.Headers.CacheControl = "private, max-age=3600";
        return Results.File(png, "image/png");
    }

    private static async Task<Media?> FindAccessibleMediaAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access)
    {
        var media = await db.Media.Include(m => m.Pool).FirstOrDefaultAsync(m => m.Id == id);
        if (media is null || !access.CanView(context, media.Pool))
        {
            // 404 (not 401) so URLs leak nothing about whether an item exists.
            return null;
        }

        return media;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "pool" : cleaned;
    }
}
