using Doctheca.Common.Oss;
using FluentAssertions;
using Xunit;

namespace Doctheca.Tests.Oss;

public class ThumbnailHelperTests
{
    [Fact]
    public void GetAllThumbnailPaths_ReturnsAllSizesInSameDirectoryAsJpg()
    {
        var paths = ThumbnailHelper.GetAllThumbnailPaths("uploads/a/b/image.png");

        paths.Should().Equal(
            "uploads/a/b/image_thumbnail.jpg",
            "uploads/a/b/image_small.jpg",
            "uploads/a/b/image_medium.jpg");
    }

    [Fact]
    public void GetThumbnailPath_WithoutDirectory_ReturnsFileNameOnly()
    {
        ThumbnailHelper.GetThumbnailPath("image.png", "small").Should().Be("image_small.jpg");
    }

    [Theory]
    [InlineData("uploads/a/image_small.jpg", true)]
    [InlineData("uploads/a/IMAGE_MEDIUM.JPG", true)]
    [InlineData("uploads/a/image.jpg", false)]
    [InlineData("uploads/a/image_small.png", false)]
    public void IsThumbnailPath_MatchesSizeSuffixWithJpgExtension(string objectPath, bool expected)
    {
        ThumbnailHelper.IsThumbnailPath(objectPath).Should().Be(expected);
    }
}
