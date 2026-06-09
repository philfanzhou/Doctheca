using System;
using System.IO;
using System.Text;
using Ruoyu.Study.DocRetrieval.Service;

namespace Ruoyu.Study.DocRetrieval.Service.Tests;

public class DocumentAdminEndpointsTests
{
    // UT-11: 加密 PDF 检测 (验证 SPEC REQ-UPLOAD-05)
    [Fact]
    public void IsEncryptedPdf_WhenPdfContainsEncrypt_ShouldReturnTrue()
    {
        // Given
        var content = "%PDF-1.4\n/Encrypt 2 0 R\n...other content...";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(content));
        var originalPosition = stream.Position;

        // When
        var result = DocumentAdminEndpoints.IsEncryptedPdf(stream, "application/pdf");

        // Then
        Assert.True(result);
        Assert.Equal(originalPosition, stream.Position); // Position 应恢复
    }

    // UT-12: 非 PDF 不检测加密
    [Fact]
    public void IsEncryptedPdf_WhenNotPdf_ShouldReturnFalse()
    {
        // Given
        using var stream = new MemoryStream();
        stream.Position = 5;
        var originalPosition = stream.Position;

        // When
        var result = DocumentAdminEndpoints.IsEncryptedPdf(stream, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        // Then
        Assert.False(result);
        Assert.Equal(originalPosition, stream.Position);
    }

    // UT-13: 正常 PDF 不误判加密
    [Fact]
    public void IsEncryptedPdf_WhenNormalPdf_ShouldReturnFalse()
    {
        // Given
        var content = "%PDF-1.4\n/Catalog 1 0 R\n/Pages 2 0 R\n...normal pdf...";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(content));
        var originalPosition = stream.Position;

        // When
        var result = DocumentAdminEndpoints.IsEncryptedPdf(stream, "application/pdf");

        // Then
        Assert.False(result);
        Assert.Equal(originalPosition, stream.Position); // Position 应恢复
    }
}