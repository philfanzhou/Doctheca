using System;
using global::Ruoyu.Study.DocLibrary.Domain.Models;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

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
        Assert.Equal(expected, DocLibraryConstants.IsValidSubject(subject));
    }

    [Fact]
    public void ValidSubjects_ContainsOnlyEnglish()
    {
        Assert.Single(DocLibraryConstants.ValidSubjects);
        Assert.Equal("英语", DocLibraryConstants.ValidSubjects[0]);
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
        Assert.Equal(expected, DocLibraryConstants.IsValidGrade(grade));
    }

    [Fact]
    public void ValidGrades_Contains13Levels()
    {
        Assert.Equal(13, DocLibraryConstants.ValidGrades.Length);
        Assert.Contains("K", DocLibraryConstants.ValidGrades);
        Assert.Contains("G1", DocLibraryConstants.ValidGrades);
        Assert.Contains("G12", DocLibraryConstants.ValidGrades);
    }

    [Fact]
    public void GradeLabels_HasLabelsForAllGrades()
    {
        Assert.Equal(13, DocLibraryConstants.GradeLabels.Count);
        Assert.Equal("Kindergarten", DocLibraryConstants.GradeLabels["K"]);
        Assert.Equal("Grade 10", DocLibraryConstants.GradeLabels["G10"]);
        Assert.Equal("Grade 12", DocLibraryConstants.GradeLabels["G12"]);
    }

    #endregion

    #region Subject Constant

    [Fact]
    public void SubjectEnglish_IsEnglish()
    {
        Assert.Equal("英语", DocLibraryConstants.SubjectEnglish);
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
        Assert.Equal(DocumentStatus.Pending, model.Status);
        Assert.Equal(string.Empty, model.Title);
    }

    #endregion

    #region DocumentStatus Constants

    [Fact]
    public void DocumentStatus_HasAllStatusValues()
    {
        Assert.Equal("pending", DocumentStatus.Pending);
        Assert.Equal("processing", DocumentStatus.Processing);
        Assert.Equal("ready", DocumentStatus.Ready);
        Assert.Equal("success", DocumentStatus.Success);
        Assert.Equal("failed", DocumentStatus.Failed);
        Assert.Equal("cancelled", DocumentStatus.Cancelled);
    }

    #endregion

    #region SegmentTypes Constants

    [Fact]
    public void SegmentTypes_HasSentenceAndQuestion()
    {
        Assert.Equal("sentence", SegmentTypes.Sentence);
        Assert.Equal("question", SegmentTypes.Question);
    }

    #endregion

    #region SourceTypes Constants

    [Fact]
    public void SourceTypes_HasAllSourceTypes()
    {
        Assert.Equal("pdf", SourceTypes.Pdf);
        Assert.Equal("word", SourceTypes.Word);
        Assert.Equal("ppt", SourceTypes.Ppt);
        Assert.Equal("unknown", SourceTypes.Unknown);
    }

    #endregion

    #region SearchMatchType Constants

    [Fact]
    public void SearchMatchType_HasAllMatchTypes()
    {
        Assert.Equal("exact_phrase", SearchMatchType.ExactPhrase);
        Assert.Equal("stemmed", SearchMatchType.Stemmed);
        Assert.Equal("exact_word", SearchMatchType.ExactWord);
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
