using System;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests;

public class ConstantsTests
{
    #region Subject Validation

    [Theory]
    [InlineData("英语", true)]
    [InlineData("数学", false)]
    [InlineData("语文", false)]
    [InlineData("", false)]
    public void IsValidSubject_ValidatesCorrectly(string subject, bool expected)
    {
        Assert.Equal(expected, DocRetrievalConstants.IsValidSubject(subject));
    }

    [Fact]
    public void ValidSubjects_ContainsOnlyEnglish()
    {
        Assert.Single(DocRetrievalConstants.ValidSubjects);
        Assert.Equal("英语", DocRetrievalConstants.ValidSubjects[0]);
    }

    #endregion

    #region Grade Validation

    [Theory]
    [InlineData("K", true)]
    [InlineData("G1", true)]
    [InlineData("G6", true)]
    [InlineData("G7", true)]
    [InlineData("G9", true)]
    [InlineData("G10", true)]
    [InlineData("G12", true)]
    [InlineData("G13", false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    [InlineData("高一", false)] // Labels are for display, not validation
    public void IsValidGrade_ValidatesCorrectly(string grade, bool expected)
    {
        Assert.Equal(expected, DocRetrievalConstants.IsValidGrade(grade));
    }

    [Fact]
    public void ValidGrades_Contains13Levels()
    {
        Assert.Equal(13, DocRetrievalConstants.ValidGrades.Length);
        Assert.Contains("K", DocRetrievalConstants.ValidGrades);
        Assert.Contains("G1", DocRetrievalConstants.ValidGrades);
        Assert.Contains("G12", DocRetrievalConstants.ValidGrades);
    }

    [Fact]
    public void GradeLabels_HasLabelsForAllGrades()
    {
        Assert.Equal(13, DocRetrievalConstants.GradeLabels.Count);
        Assert.Equal("幼儿园", DocRetrievalConstants.GradeLabels["K"]);
        Assert.Equal("高一", DocRetrievalConstants.GradeLabels["G10"]);
        Assert.Equal("高三", DocRetrievalConstants.GradeLabels["G12"]);
    }

    #endregion

    #region Subject Constant

    [Fact]
    public void SubjectEnglish_IsChineseEnglish()
    {
        Assert.Equal("英语", DocRetrievalConstants.SubjectEnglish);
    }

    #endregion

    #region SearchConfig Defaults

    [Fact]
    public void OpenSearchOptions_HasCorrectDefaults()
    {
        var options = new OpenSearchOptions();
        Assert.Equal("http://localhost:9200", options.Url);
        Assert.Equal("docretrieval-segments", options.IndexName);
    }

    [Fact]
    public void QdrantOptions_HasCorrectDefaults()
    {
        var options = new QdrantOptions();
        Assert.Equal("http://localhost:6333", options.Url);
        Assert.Equal("docretrieval_segments", options.CollectionName);
    }

    [Fact]
    public void EmbeddingOptions_HasCorrectDefaults()
    {
        var options = new EmbeddingOptions();
        Assert.Equal("https://api.siliconflow.cn/v1/embeddings", options.ApiUrl);
        Assert.Equal("BAAI/bge-large-en-v1.5", options.Model);
    }

    #endregion

    #region SearchFilterModel

    [Fact]
    public void SearchFilterModel_DefaultValues_AreNull()
    {
        var filter = new SearchFilterModel();
        Assert.Null(filter.DocumentTitle);
        Assert.Null(filter.Subject);
        Assert.Null(filter.Grade);
        Assert.Null(filter.Year);
    }

    #endregion

    #region SearchResultModel

    [Fact]
    public void SearchResultModel_DefaultValues()
    {
        var result = new SearchResultModel();
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

    #region DocumentModel Tests

    [Fact]
    public void DocumentModel_NewInstance_HasDefaultValues()
    {
        var model = new DocumentModel();
        Assert.NotEqual(Guid.Empty, model.Id);
        Assert.Equal("en", model.Language);
        Assert.Equal("pending", model.Status);
        Assert.Equal(string.Empty, model.Title);
    }

    #endregion

    #region ParsedDocumentModel Tests

    [Fact]
    public void ParsedDocument_InitializesEmptyLists()
    {
        var doc = new ParsedDocument();
        Assert.NotNull(doc.Pages);
        Assert.Empty(doc.Pages);
    }

    [Fact]
    public void ParsedPage_InitializesEmptyLists()
    {
        var page = new ParsedPage();
        Assert.NotNull(page.Segments);
        Assert.NotNull(page.Questions);
        Assert.Empty(page.Segments);
        Assert.Empty(page.Questions);
    }

    [Fact]
    public void ParsedSegment_HasDefaultSegmentType()
    {
        var segment = new ParsedSegment();
        Assert.Equal("sentence", segment.SegmentType);
    }

    #endregion
}
