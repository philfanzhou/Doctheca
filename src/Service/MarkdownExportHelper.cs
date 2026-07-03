using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Shared helper for Markdown/HTML export logic: image path replacement, ZIP building, HTML document wrapping.
/// </summary>
internal static class MarkdownExportHelper
{
    /// <summary>
    /// Replace S3 image paths in markdown with relative paths (images/name).
    /// </summary>
    public static string ReplaceImagePathsRelative(string markdown, IReadOnlyList<DocumentParseImageModel> images)
    {
        foreach (var img in images)
        {
            markdown = markdown.Replace($"({img.ImagePath})", $"(images/{img.ImageName})");
            markdown = markdown.Replace($"src=\"{img.ImagePath}\"", $"src=\"images/{img.ImageName}\"");
            markdown = markdown.Replace($"src='{img.ImagePath}'", $"src='images/{img.ImageName}'");
        }
        return markdown;
    }

    /// <summary>
    /// Replace S3 image paths in markdown with base64 data URIs for self-contained HTML.
    /// </summary>
    public static async Task<string> ReplaceImagePathsBase64Async(
        string markdown,
        IReadOnlyList<DocumentParseImageModel> images,
        IOssService ossService,
        ILogger logger)
    {
        foreach (var img in images)
        {
            try
            {
                using var imgStream = await ossService.DownloadAsync(img.ImagePath);
                using var imgMs = new MemoryStream();
                await imgStream.CopyToAsync(imgMs);
                var base64 = Convert.ToBase64String(imgMs.ToArray());
                var dataUri = $"data:{img.ContentType};base64,{base64}";

                markdown = markdown.Replace($"({img.ImagePath})", $"({dataUri})");
                markdown = markdown.Replace($"src=\"{img.ImagePath}\"", $"src=\"{dataUri}\"");
                markdown = markdown.Replace($"src='{img.ImagePath}'", $"src='{dataUri}'");
                markdown = markdown.Replace($"(images/{img.ImageName})", $"({dataUri})");
                markdown = markdown.Replace($"src=\"images/{img.ImageName}\"", $"src=\"{dataUri}\"");
                markdown = markdown.Replace($"src='images/{img.ImageName}'", $"src='{dataUri}'");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download image for HTML export: {ImagePath}", img.ImagePath);
            }
        }
        return markdown;
    }

    /// <summary>
    /// Replace S3 image paths in markdown with presigned URLs for in-browser viewing.
    /// </summary>
    public static async Task<string> ReplaceImagePathsPresignedAsync(
        string markdown,
        IReadOnlyList<DocumentParseImageModel> images,
        IOssService ossService,
        ILogger logger)
    {
        foreach (var img in images)
        {
            try
            {
                var presignedUrl = await ossService.GetPresignedUrlAsync(img.ImagePath, 3600);
                markdown = markdown.Replace($"({img.ImagePath})", $"({presignedUrl})");
                markdown = markdown.Replace($"(images/{img.ImageName})", $"({presignedUrl})");
                markdown = markdown.Replace($"src=\"{img.ImagePath}\"", $"src=\"{presignedUrl}\"");
                markdown = markdown.Replace($"src=\"images/{img.ImageName}\"", $"src=\"{presignedUrl}\"");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to generate presigned URL for image: {ImagePath}", img.ImagePath);
            }
        }
        return markdown;
    }

    /// <summary>
    /// Build a ZIP stream containing the markdown file and images directory.
    /// </summary>
    public static async Task<MemoryStream> BuildMarkdownZipAsync(
        string fileName,
        string markdownContent,
        IReadOnlyList<DocumentParseImageModel> images,
        IOssService ossService,
        ILogger logger)
    {
        var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var mdEntry = archive.CreateEntry(
                Path.GetFileNameWithoutExtension(fileName) + ".md",
                System.IO.Compression.CompressionLevel.Optimal);
            using (var mdStream = mdEntry.Open())
            using (var writer = new StreamWriter(mdStream))
            {
                await writer.WriteAsync(markdownContent);
            }

            foreach (var img in images)
            {
                try
                {
                    using var imgStream = await ossService.DownloadAsync(img.ImagePath);
                    var imgEntry = archive.CreateEntry($"images/{img.ImageName}", System.IO.Compression.CompressionLevel.Fastest);
                    using var entryStream = imgEntry.Open();
                    await imgStream.CopyToAsync(entryStream);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to download image for export: {ImagePath}", img.ImagePath);
                }
            }
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Build a self-contained HTML document from markdown content with inline CSS.
    /// </summary>
    public static MemoryStream BuildHtmlStream(string fileName, string markdownContent)
    {
        var htmlBody = Markdig.Markdown.ToHtml(markdownContent);

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"UTF-8\">");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine($"  <title>{System.Net.WebUtility.HtmlEncode(fileName)}</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;");
        html.AppendLine("           max-width: 800px; margin: 0 auto; padding: 20px; line-height: 1.6; color: #333; }");
        html.AppendLine("    img { max-width: 100%; height: auto; }");
        html.AppendLine("    table { border-collapse: collapse; width: 100%; }");
        html.AppendLine("    th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
        html.AppendLine("    blockquote { border-left: 4px solid #ddd; margin: 0; padding-left: 16px; color: #666; }");
        html.AppendLine("    code { background: #f4f4f4; padding: 2px 6px; border-radius: 3px; }");
        html.AppendLine("    pre { background: #f4f4f4; padding: 16px; overflow-x: auto; border-radius: 6px; }");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine(htmlBody);
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        var htmlBytes = Encoding.UTF8.GetBytes(html.ToString());
        var htmlStream = new MemoryStream(htmlBytes);
        return htmlStream;
    }
}
