using Shoebox.Web.Data;
using Shoebox.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Shoebox.Web.Api;

public static class PhotoEndpoints
{
    public static void MapPhotoApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/p/{code}/photos", UploadAsync).DisableAntiforgery();
        api.MapGet("/photos/{id:guid}/thumb", ServeThumbAsync);
        api.MapGet("/photos/{id:guid}/display", ServeDisplayAsync);
        api.MapGet("/photos/{id:guid}/original", ServeOriginalAsync);
        api.MapDelete("/photos/{id:guid}", DeletePhotoAsync);
        api.MapPost("/photos/{id:guid}/reprocess", ReprocessAsync);
        api.MapPost("/photos/{id:guid}/like", ToggleLikeAsync).DisableAntiforgery();
        api.MapGet("/p/{code}/zip", DownloadZipAsync);
        api.MapGet("/p/{code}/qr", QrCodeAsync);
    }

    private static async Task<IResult> UploadAsync(
        string code,
        HttpRequest request,
        AppDbContext db,
        PoolService pools,
        PhotoService photos,
        PoolAccessService access,
        UploaderIdentity identity)
    {
        var pool = await pools.FindByCodeAsync(code);
        if (pool is null)
        {
            return Results.NotFound();
        }

        if (!access.CanView(request.HttpContext, pool))
        {
            return Results.Unauthorized();
        }

        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Expected multipart form data." });
        }

        var form = await request.ReadFormAsync();
        var uploaderName = form["uploaderName"].ToString().Trim();
        if (uploaderName.Length is 0 or > 80)
        {
            return Results.BadRequest(new { error = "Please tell us who you are (1-80 characters)." });
        }

        if (form.Files.Count == 0)
        {
            return Results.BadRequest(new { error = "No files in upload." });
        }

        var uid = identity.GetOrCreateUid(request.HttpContext);
        identity.RememberName(request.HttpContext, uploaderName);

        var results = new List<UploadResult>();
        foreach (var file in form.Files)
        {
            results.Add(await photos.SaveAsync(pool, file, uploaderName, uid, request.HttpContext.RequestAborted));
        }

        return Results.Ok(new { results });
    }

    private static async Task<IResult> ServeThumbAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access, StoragePaths paths)
    {
        var photo = await FindAccessiblePhotoAsync(id, context, db, access);
        if (photo is null)
        {
            return Results.NotFound();
        }

        var thumbPath = paths.ThumbFile(photo.PoolId, photo.Id);
        if (!photo.HasThumbnail || !File.Exists(thumbPath))
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(thumbPath, "image/webp");
    }

    private static async Task<IResult> ServeDisplayAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access, StoragePaths paths)
    {
        var photo = await FindAccessiblePhotoAsync(id, context, db, access);
        if (photo is null)
        {
            return Results.NotFound();
        }

        var displayPath = paths.DisplayFile(photo.PoolId, photo.Id);
        if (!photo.HasThumbnail || !File.Exists(displayPath))
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
        var photo = await FindAccessiblePhotoAsync(id, context, db, access);
        if (photo is null)
        {
            return Results.NotFound();
        }

        var path = paths.OriginalFile(photo.PoolId, photo.Id, photo.Extension);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(path, photo.ContentType,
            fileDownloadName: download ? photo.OriginalFileName : null,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> ReprocessAsync(
        Guid id, HttpContext context, AppDbContext db, PhotoService photos, PoolAccessService access)
    {
        var photo = await db.Photos.Include(p => p.Pool).FirstOrDefaultAsync(p => p.Id == id);
        if (photo is null)
            return Results.NotFound();
        if (!access.IsAdmin(context, photo.PoolId))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var ok = await photos.ReprocessAsync(photo, context.RequestAborted);
        return ok
            ? Results.Ok(new { status = "reprocessed" })
            : Results.UnprocessableEntity(new { error = "Could not render image (original missing or unreadable)" });
    }

    private static async Task<IResult> DeletePhotoAsync(
        Guid id, HttpContext context, AppDbContext db, PhotoService photos,
        PoolAccessService access, UploaderIdentity identity)
    {
        var photo = await db.Photos.Include(p => p.Pool).FirstOrDefaultAsync(p => p.Id == id);
        if (photo is null)
        {
            return Results.NotFound();
        }

        var isAdmin = access.IsAdmin(context, photo.PoolId);
        var isOwner = photo.UploaderUid == identity.GetOrCreateUid(context);
        if (!isAdmin && !isOwner)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        await photos.DeleteAsync(photo);
        return Results.NoContent();
    }

    private static async Task<IResult> ToggleLikeAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access, UploaderIdentity identity)
    {
        var photo = await FindAccessiblePhotoAsync(id, context, db, access);
        if (photo is null)
        {
            return Results.NotFound();
        }

        var uid = identity.GetOrCreateUid(context);
        var existing = await db.Likes.FirstOrDefaultAsync(l => l.PhotoId == id && l.UploaderUid == uid);

        bool liked;
        if (existing is null)
        {
            db.Likes.Add(new PhotoLike { PhotoId = id, UploaderUid = uid, CreatedAt = DateTime.UtcNow });
            liked = true;
        }
        else
        {
            db.Likes.Remove(existing);
            liked = false;
        }

        await db.SaveChangesAsync();

        var count = await db.Likes.CountAsync(l => l.PhotoId == id);
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

        var query = db.Photos.Where(p => p.PoolId == pool.Id);
        if (mode == "others")
        {
            var uid = identity.GetOrCreateUid(context);
            query = query.Where(p => p.UploaderUid != uid);
        }

        var photos = await query.OrderBy(p => p.TakenAt ?? p.UploadedAt).ToListAsync();
        if (photos.Count == 0)
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
        await zip.WriteAsync(photos, context.Response.Body, context.RequestAborted);
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

    private static async Task<Photo?> FindAccessiblePhotoAsync(
        Guid id, HttpContext context, AppDbContext db, PoolAccessService access)
    {
        var photo = await db.Photos.Include(p => p.Pool).FirstOrDefaultAsync(p => p.Id == id);
        if (photo is null || !access.CanView(context, photo.Pool))
        {
            // 404 (not 401) so URLs leak nothing about whether a photo exists.
            return null;
        }

        return photo;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "pool" : cleaned;
    }
}
