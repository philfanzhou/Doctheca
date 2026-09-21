using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Shared helper for Markdown/HTML export logic: image path replacement, ZIP building, HTML document wrapping.
/// Image bytes and URLs are supplied by caller-provided delegates so the same logic serves both
/// legacy OSS-backed parses and StructaDoc-backed parses (ADR-0009).
/// </summary>
internal static class MarkdownExportHelper
{
    /// <summary>
    /// Replace stored image references in markdown with relative paths (images/name) for ZIP packaging.
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
    /// Replace image references in markdown with base64 data URIs for self-contained HTML.
    /// </summary>
    public static async Task<string> ReplaceImagePathsBase64Async(
        string markdown,
        IReadOnlyList<DocumentParseImageModel> images,
        Func<DocumentParseImageModel, Task<Stream?>> imageOpener,
        ILogger logger)
    {
        foreach (var img in images)
        {
            try
            {
                await using var imgStream = await imageOpener(img);
                if (imgStream == null)
                {
                    logger.LogWarning("Image source unavailable for HTML export: {ImageName}", img.ImageName);
                    continue;
                }

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
                logger.LogWarning(ex, "Failed to download image for HTML export: {ImageName}", img.ImageName);
            }
        }
        return markdown;
    }

    /// <summary>
    /// Replace image references in markdown with viewer URLs resolved per image
    /// (presigned OSS URLs for legacy parses, DocLibrary proxy URLs for StructaDoc parses).
    /// </summary>
    public static async Task<string> ReplaceImagePathsAsync(
        string markdown,
        IReadOnlyList<DocumentParseImageModel> images,
        Func<DocumentParseImageModel, Task<string?>> urlResolver,
        ILogger logger)
    {
        foreach (var img in images)
        {
            try
            {
                var url = await urlResolver(img);
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                markdown = markdown.Replace($"({img.ImagePath})", $"({url})");
                markdown = markdown.Replace($"(images/{img.ImageName})", $"({url})");
                markdown = markdown.Replace($"src=\"{img.ImagePath}\"", $"src=\"{url}\"");
                markdown = markdown.Replace($"src=\"images/{img.ImageName}\"", $"src=\"{url}\"");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve image URL: {ImageName}", img.ImageName);
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
        Func<DocumentParseImageModel, Task<Stream?>> imageOpener,
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
                    await using var imgStream = await imageOpener(img);
                    if (imgStream == null)
                    {
                        logger.LogWarning("Image source unavailable for export: {ImageName}", img.ImageName);
                        continue;
                    }

                    var imgEntry = archive.CreateEntry($"images/{img.ImageName}", System.IO.Compression.CompressionLevel.Fastest);
                    await using var entryStream = imgEntry.Open();
                    await imgStream.CopyToAsync(entryStream);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to download image for export: {ImageName}", img.ImageName);
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
