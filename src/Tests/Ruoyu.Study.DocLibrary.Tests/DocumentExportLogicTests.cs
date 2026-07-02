using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Markdig;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class DocumentExportLogicTests
{
    [Fact]
    public void MarkdownPathReplacement_ReplacesS3PathWithRelativePath()
    {
        // Arrange
        var markdown = "![image](documents/mineru/task-123/abc.jpg)";
        var imagePath = "documents/mineru/task-123/abc.jpg";
        var imageName = "abc.jpg";

        // Act
        var result = markdown.Replace($"({imagePath})", $"(images/{imageName})");

        // Assert
        result.Should().Be("![image](images/abc.jpg)");
    }

    [Fact]
    public void MarkdownPathReplacement_ReplacesSrcAttribute()
    {
        // Arrange
        var markdown = "<img src=\"documents/mineru/task-123/abc.jpg\"/>";
        var imagePath = "documents/mineru/task-123/abc.jpg";
        var imageName = "abc.jpg";

        // Act
        var result = markdown.Replace($"src=\"{imagePath}\"", $"src=\"images/{imageName}\"");

        // Assert
        result.Should().Be("<img src=\"images/abc.jpg\"/>");
    }

    [Fact]
    public void MarkdownPathReplacement_ReplacesSrcAttribute_SingleQuotes()
    {
        // Arrange
        var markdown = "<img src='documents/mineru/task-123/abc.jpg'/>";
        var imagePath = "documents/mineru/task-123/abc.jpg";
        var imageName = "abc.jpg";

        // Act
        var result = markdown.Replace($"src='{imagePath}'", $"src='images/{imageName}'");

        // Assert
        result.Should().Be("<img src='images/abc.jpg'/>");
    }

    [Fact]
    public void MarkdownPathReplacement_ReplacesMixedQuoteFormats()
    {
        // Arrange
        var markdown = "![i](documents/mineru/t/a.jpg)\n<img src=\"documents/mineru/t/a.jpg\"/>\n<img src='documents/mineru/t/a.jpg'/>";
        var imagePath = "documents/mineru/t/a.jpg";
        var imageName = "a.jpg";

        // Act
        var result = markdown
            .Replace($"({imagePath})", $"(images/{imageName})")
            .Replace($"src=\"{imagePath}\"", $"src=\"images/{imageName}\"")
            .Replace($"src='{imagePath}'", $"src='images/{imageName}'");

        // Assert
        result.Should().Be("![i](images/a.jpg)\n<img src=\"images/a.jpg\"/>\n<img src='images/a.jpg'/>");
        result.Should().NotContain("documents/");
    }

    [Fact]
    public void MarkdownPathReplacement_HandlesMultipleImages()
    {
        // Arrange
        var markdown = "![a](documents/mineru/task-1/a.jpg)\n\n![b](documents/mineru/task-1/b.png)";
        var images = new[]
        {
            (ImagePath: "documents/mineru/task-1/a.jpg", ImageName: "a.jpg"),
            (ImagePath: "documents/mineru/task-1/b.png", ImageName: "b.png"),
        };

        // Act
        var result = markdown;
        foreach (var img in images)
        {
            result = result.Replace($"({img.ImagePath})", $"(images/{img.ImageName})");
        }

        // Assert
        result.Should().Be("![a](images/a.jpg)\n\n![b](images/b.png)");
    }

    [Fact]
    public void MarkdownPathReplacement_DoesNotReplaceIfPathNotFound()
    {
        // Arrange
        var markdown = "![image](other/path.jpg)";
        var imagePath = "documents/mineru/task-123/abc.jpg";
        var imageName = "abc.jpg";

        // Act
        var result = markdown.Replace($"({imagePath})", $"(images/{imageName})");

        // Assert
        result.Should().Be("![image](other/path.jpg)");
    }

    [Fact]
    public void HtmlExport_ReplacesS3PathWithDataUri()
    {
        // Arrange
        var markdown = "![image](documents/mineru/task-123/abc.jpg)";
        var imagePath = "documents/mineru/task-123/abc.jpg";
        var base64 = "iVBORw0KGgo=";
        var contentType = "image/jpeg";
        var dataUri = $"data:{contentType};base64,{base64}";

        // Act
        var result = markdown.Replace($"({imagePath})", $"({dataUri})");

        // Assert
        result.Should().Be($"![image]({dataUri})");
    }

    [Fact]
    public void Markdig_ConvertsMarkdownToHtml()
    {
        // Arrange
        var md = "# Hello\n\nThis is **bold** text.\n\n- Item 1\n- Item 2";

        // Act
        var html = Markdown.ToHtml(md);

        // Assert
        html.Should().Contain("<h1>Hello</h1>");
        html.Should().Contain("<strong>bold</strong>");
        html.Should().Contain("<li>Item 1</li>");
    }

    [Fact]
    public void Markdig_ConvertsMarkdownWithImageToHtml()
    {
        // Arrange
        var md = "![alt text](images/test.jpg)";

        // Act
        var html = Markdown.ToHtml(md);

        // Assert
        html.Should().Contain("<img");
        html.Should().Contain("src=\"images/test.jpg\"");
        html.Should().Contain("alt=\"alt text\"");
    }

    [Fact]
    public void HtmlTemplate_GeneratesValidHtmlDocument()
    {
        // Arrange
        var fileName = "Test Document.pdf";
        var htmlBody = "<h1>Hello</h1><p>World</p>";

        // Act
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"UTF-8\">");
        html.AppendLine($"  <title>{System.Net.WebUtility.HtmlEncode(fileName)}</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body { font-family: -apple-system, sans-serif; }");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine(htmlBody);
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        var result = html.ToString();

        // Assert
        result.Should().Contain("<!DOCTYPE html>");
        result.Should().Contain("<title>Test Document.pdf</title>");
        result.Should().Contain("<h1>Hello</h1>");
        result.Should().Contain("<style>");
    }

    [Fact]
    public async Task ZipExport_ContainsMarkdownAndImages()
    {
        // Arrange
        var markdownContent = "# Test\n\n![img](images/test.jpg)";
        var imageName = "test.jpg";
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header bytes

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var mdEntry = archive.CreateEntry("test.md", CompressionLevel.Optimal);
            using (var mdStream = mdEntry.Open())
            using (var writer = new StreamWriter(mdStream))
            {
                await writer.WriteAsync(markdownContent);
            }

            var imgEntry = archive.CreateEntry($"images/{imageName}", CompressionLevel.Fastest);
            using var entryStream = imgEntry.Open();
            await entryStream.WriteAsync(imageBytes);
        }

        // Act
        ms.Position = 0;
        using var readArchive = new ZipArchive(ms, ZipArchiveMode.Read);

        // Assert
        readArchive.Entries.Should().HaveCount(2);
        readArchive.Entries.Should().Contain(e => e.FullName == "test.md");
        readArchive.Entries.Should().Contain(e => e.FullName == $"images/{imageName}");

        var readMdEntry = readArchive.GetEntry("test.md")!;
        using var mdReader = new StreamReader(readMdEntry.Open());
        var mdContent = await mdReader.ReadToEndAsync();
        mdContent.Should().Be(markdownContent);

        var readImgEntry = readArchive.GetEntry($"images/{imageName}")!;
        using var readImgStream = readImgEntry.Open();
        var readBytes = new byte[imageBytes.Length];
        await readImgStream.ReadAsync(readBytes);
        readBytes.Should().Equal(imageBytes);
    }

    [Fact]
    public void Base64Conversion_RoundTripsCorrectly()
    {
        // Arrange
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header bytes
        var contentType = "image/png";

        // Act
        var base64 = Convert.ToBase64String(imageBytes);
        var dataUri = $"data:{contentType};base64,{base64}";
        var decoded = Convert.FromBase64String(base64);

        // Assert
        decoded.Should().Equal(imageBytes);
        dataUri.Should().StartWith("data:image/png;base64,");
    }
}
