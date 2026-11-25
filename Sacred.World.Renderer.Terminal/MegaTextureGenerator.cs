using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Sacred.Core.World.Sector;
using Sacred.World.Map;
using Sacred.World.Rendering;

namespace Sacred.World.Renderer.Terminal;

/// <summary>
/// Renders the whole world into map-aligned PNG tiles. Output pixel (u, v) of the mega texture is game-map pixel
/// (u / scale, v / scale) of the combined 2048 x 2816 map picture (Underworld rows above Ancaria), so
/// <see cref="WorldMapProjection.WorldToMap"/> (kind Underworld = no row offset) maps a world tile position to it directly.
/// The game map is the isometric view stretched vertically (x ~ tx - ty, y ~ tx + ty at equal scale); each tile is rendered
/// in the undistorted isometric view and area-resampled into that space.
/// </summary>
internal static class MegaTextureGenerator
{
    private const float StepX = 0.007446f;  // map pixels per isometric pixel, horizontally (WorldMapProjection: 48 * 0.007446 per tile)
    private const float StepY = 0.014914f;  // vertically (24 * 0.014914 per tile)
    private const int MapWidth = 2048, MapHeight = 2816, UnderworldRows = 768;

    public static async Task<int> RunAsync(RendererOptions o, SacredWorldArchive world, WorldRenderSession renderer)
    {
        var scale = o.MegaScale;
        var tile = o.MegaTile;
        var ss = o.Supersampling;
        var cols = (int)Math.Ceiling((double)MapWidth * scale / tile);
        var rowsAll = (int)Math.Ceiling((double)MapHeight * scale / tile);
        var firstRow = o.MegaAncariaOnly ? (int)Math.Floor((double)UnderworldRows * scale / tile) : 0;
        var rowMin = Math.Max(firstRow, o.MegaRowMin);
        var rowMax = Math.Min(rowsAll - 1, o.MegaRowMax);
        var rowCount = rowMax - rowMin + 1;
        var bandStart = rowMin + rowCount * o.MegaPart / o.MegaParts;
        var bandEnd = rowMin + rowCount * (o.MegaPart + 1) / o.MegaParts - 1;
        var existing = new HashSet<SectorCoord>(world.SectorCoordinates);
        var sw = Stopwatch.StartNew();
        int done = 0, empty = 0, skipped = 0, failed = 0;
        var isoRangeX = tile / (StepX * scale);          // isometric pixels one tile spans horizontally
        var renderZoom = StepY * scale * ss;             // exact vertical scale
        var renderW = (int)Math.Round(isoRangeX * renderZoom);
        var renderH = tile * ss;
        RendererLog.Info($"Mega: scale {scale}, tile {tile}, {cols} cols, rows {bandStart}..{bandEnd} of {rowsAll}, render {renderW}x{renderH}, zoom {renderZoom:F5}, output {MapWidth * scale}x{MapHeight * scale}");
        var dir = Path.Combine(o.OutputDirectory, $"s{scale}");
        for (var row = bandStart; row <= bandEnd; row++)
        for (var col = 0; col < cols; col++)
        {
            var path = Path.Combine(dir, $"{row:D3}_{col:D3}.png");
            if (o.SkipExisting && File.Exists(path)) { skipped++; continue; }
            var map = new Vector2((col + 0.5f) * tile / scale, (row + 0.5f) * tile / scale);
            var center = WorldMapProjection.MapToWorld(map, MapWidth, WorldMapKind.Underworld);
            var cs = new SectorCoord((int)MathF.Floor(center.X / Sector.TileCount), (int)MathF.Floor(center.Y / Sector.TileCount));
            var any = false;
            for (var dy = -1; dy <= 1 && !any; dy++)
            for (var dx = -1; dx <= 1 && !any; dx++)
                any = existing.Contains(new SectorCoord(cs.X + dx, cs.Y + dy));
            if (!any) { empty++; continue; }
            try
            {
                var image = (await renderer.RenderAsync(center, renderW, renderH, renderZoom, o, null, drawCenterMarker: false)).Complete;
                // The canvas background (12, 15, 17) is where no sector exists: make it transparent before the area filter.
                var px = image.Pixels;
                for (var i = 0; i < px.Length; i += 4)
                    if (px[i] == 12 && px[i + 1] == 15 && px[i + 2] == 17) px[i + 3] = 0;
                var output = RgbaImageDownsampler.Resize(image, tile, tile);
                WritePng(path, output);
                done++;
            }
            catch (Exception e) { failed++; Console.Error.WriteLine($"tile {row},{col}: {e.Message}"); }
            if ((done + failed) % 8 == 0) renderer.ReleaseBatch();
            if ((done + failed) % 50 == 0)
                RendererLog.Info($"Mega progress: row {row} col {col}, {done} done, {empty} empty, {skipped} existing, {failed} failed, {sw.Elapsed.TotalSeconds:F0}s");
        }
        RendererLog.Info($"Mega finished: {done} done, {empty} empty, {skipped} existing, {failed} failed in {sw.Elapsed.TotalSeconds:F0}s");
        return failed == 0 ? 0 : 1;
    }

    private static void WritePng(string path, RgbaImage image)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bgra = new byte[image.Pixels.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = image.Pixels[i + 2]; bgra[i + 1] = image.Pixels[i + 1]; bgra[i + 2] = image.Pixels[i]; bgra[i + 3] = image.Pixels[i + 3];
        }
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(image.Width, image.Height, image.Stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            bitmap.Save(path + ".tmp", System.Drawing.Imaging.ImageFormat.Png);
        }
        finally { handle.Free(); }
        File.Move(path + ".tmp", path, true);
    }
}
