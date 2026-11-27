using global::Doctheca.Domain.Models;
using Xunit;

namespace Doctheca.Tests;

public class ConstantsTests
{
    #region SearchConfig Defaults

    [Fact]
    public void OpenSearchOptions_HasCorrectDefaults()
    {
        var options = new OpenSearchOptions();
        Assert.Equal("http://localhost:9200", options.Url);
        Assert.Equal("doctheca-segments", options.IndexName);
    }

    #endregion

    #region SearchFilter

    [Fact]
    public void SearchFilterModel_DefaultValues_AreNull()
    {
        var filter = new SearchFilter();
        Assert.Null(filter.DocumentTitle);
        Assert.Null(filter.Subject);
        Assert.Null(filter.Grade);
        Assert.Null(filter.Year);
    }

    #endregion

    #region SearchResult

    [Fact]
    public void SearchResultModel_DefaultValues()
    {
        var result = new SearchResult();
        Assert.Equal(string.Empty, result.DocumentName);
        Assert.Equal(0, result.PageNumber);
        Assert.Equal(string.Empty, result.AssociatedText);
        Assert.Equal(0, result.Score);
        Assert.Equal(string.Empty, result.MatchType);
        Assert.Equal(string.Empty, result.SegmentId);
        Assert.Equal(0, result.StartOffset);
        Assert.Equal(0, result.EndOffset);
    }

    #endregion

    #region SearchMatchType Constants

    [Fact]
    public void SearchMatchType_HasAllMatchTypes()
    {
        Assert.Equal("exact_phrase", SearchMatchType.ExactPhrase);
        Assert.Equal("stemmed", SearchMatchType.Stemmed);
    }

    #endregion
}
