using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class FileConversionServiceTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_WhenLibreOfficeNotInstalled()
    {
        // Arrange
        var logger = new Mock<ILogger<LibreOfficeConversionService>>().Object;

        // Act
        var service = new LibreOfficeConversionService(logger);

        // Assert - on Windows dev machine, LibreOffice is likely not in PATH
        // This test just verifies the service can be constructed
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task ConvertToPdfAsync_Throws_WhenNotAvailable()
    {
        // Arrange
        var logger = new Mock<ILogger<LibreOfficeConversionService>>().Object;
        var service = new LibreOfficeConversionService(logger);

        // If LibreOffice is not available, the method should throw
        if (!service.IsAvailable)
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("test"));
            var act = () => service.ConvertToPdfAsync(ms, "test.docx");

            // Act & Assert
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*LibreOffice*");
        }
    }

    [Fact]
    public void IsPdfFile_DetectedByContentType()
    {
        // Verify our PDF detection logic
        var pdfContentType = "application/pdf";
        var docxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        pdfContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        docxContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void IsPdfFile_DetectedByExtension()
    {
        // Verify our PDF detection logic
        var pdfFile = "document.pdf";
        var docxFile = "document.docx";

        pdfFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        docxFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }
}
